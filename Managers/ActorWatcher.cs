﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Actors;
using Dalamud.Game.Internal;
using Dalamud.Plugin;
using RezPls.Enums;

namespace RezPls.Managers
{
    public class ActorWatcher : IDisposable
    {
        private          bool                   _enabled = false;
        private readonly DalamudPluginInterface _pluginInterface;
        private readonly IntPtr                 _actorTablePtr;
        private const    int                    ActorTableLength       = 424;
        private const    int                    ActorTablePlayerLength = 256;

        public readonly Dictionary<uint, uint>      RezList        = new(128);
        public readonly Dictionary<uint, string>    ActorNames     = new();
        public readonly Dictionary<uint, Position3> ActorPositions = new();
        public          (uint, uint)                PlayerRez      = (0, uint.MaxValue);

        public ActorWatcher(DalamudPluginInterface pluginInterface)
        {
            _pluginInterface                         =  pluginInterface;
            _pluginInterface.Framework.OnUpdateEvent += OnFrameworkUpdate;

            _actorTablePtr = BaseAddressResolver.DebugScannedValues["ClientStateAddressResolver"]
                .Find(kvp => kvp.Item1 == "ActorTable").Item2;
        }

        public void Enable()
        {
            if (_enabled)
                return;

            _pluginInterface.Framework.OnUpdateEvent += OnFrameworkUpdate;
            _enabled                                 =  true;
        }

        public void Disable()
        {
            if (!_enabled)
                return;

            _pluginInterface.Framework.OnUpdateEvent -= OnFrameworkUpdate;
            _enabled                                 =  false;
            RezList.Clear();
            PlayerRez = (0, 0);
        }

        public void Dispose()
            => Disable();

        public unsafe (Job job, byte level) CurrentPlayerJob()
        {
            var player = *(byte**) _actorTablePtr;
            if (player == null || !IsPlayer(player))
                return (Job.ADV, 0);

            return (PlayerJob(player), PlayerLevel(player));
        }

        private static unsafe ushort GetCurrentCast(byte* actorPtr)
        {
            const int    currentCastTypeOffset = 0x1B82;
            const int    currentCastIdOffset   = 0x1B84;
            const ushort currentCastIdSpell    = 0x01;

            if (*(ushort*) (actorPtr + currentCastTypeOffset) != currentCastIdSpell)
                return 0;

            return *(ushort*) (actorPtr + currentCastIdOffset);
        }

        private static unsafe uint GetCastTarget(byte* actorPtr)
        {
            const int currentCastTargetOffset = 0x1B90;
            return *(uint*) (actorPtr + currentCastTargetOffset);
        }

        private static unsafe uint GetActorId(byte* actorPtr)
        {
            const int actorIdOffset = 0x74;
            return *(uint*) (actorPtr + actorIdOffset);
        }

        private static unsafe string GetActorName(byte* actorPtr)
        {
            const int actorNameOffset = 0x30;
            const int actorNameLength = 30;

            return Marshal.PtrToStringAnsi(new IntPtr(actorPtr) + actorNameOffset, actorNameLength).TrimEnd('\0');
        }

        private static unsafe Position3 GetActorPosition(byte* actorPtr)
        {
            const int actorPositionOffset = 0xA0;
            return new Position3
            {
                X = *(float*) (actorPtr + actorPositionOffset),
                Y = *(float*) (actorPtr + actorPositionOffset + 8),
                Z = *(float*) (actorPtr + actorPositionOffset + 4),
            };
        }

        private static unsafe bool IsCastingResurrection(byte* actorPtr)
        {
            return GetCurrentCast(actorPtr) switch
            {
                173   => true, // ACN, SMN, SCH
                125   => true, // CNH, WHM
                3603  => true, // AST
                18317 => true, // BLU
                208   => true, // WHM LB3
                4247  => true, // SCH LB3
                4248  => true, // AST LB3
                7523  => true, // RDM
                22345 => true, // Lost Sacrifice, Bozja
                20730 => true, // Lost Arise, Bozja
                12996 => true, // Raise L, Eureka
                _     => false,
            };
        }

        private static unsafe bool IsPlayer(byte* actorPtr)
        {
            const int  objectKindOffset = 0x8C;
            const byte playerObjectKind = (byte) ObjectKind.Player;

            return *(actorPtr + objectKindOffset) == playerObjectKind;
        }

        private static unsafe bool IsDead(byte* actorPtr)
        {
            const int currentHpOffset = 0x1C4;

            return *(int*) (actorPtr + currentHpOffset) == 0;
        }

        private static unsafe Job PlayerJob(byte* actorPtr)
        {
            const int jobOffset = 0x1E2;
            return *(Job*) (actorPtr + jobOffset);
        }

        private static unsafe byte PlayerLevel(byte* actorPtr)
        {
            const int jobOffset = 0x1E3;
            return *(actorPtr + jobOffset);
        }

        private static unsafe bool IsRaised(byte* actorPtr)
        {
            const int statusEffectsOffset = 0x19F8;
            const int statusEffectSize    = 12;
            const int maxStatusEffects    = 20;

            var start = actorPtr + statusEffectsOffset;
            var end   = start + statusEffectSize * maxStatusEffects;
            for (; start < end; start += 12)
            {
                var id = *(ushort*) start;
                switch (id)
                {
                    case 148:
                    case 1140:
                        return true;
                    case 0: return false;
                }
            }

            return false;
        }

        private unsafe void IterateActors()
        {
            var current = (byte**) _actorTablePtr;
            var end     = current + ActorTablePlayerLength;
            for (; current < end; current += 2)
            {
                var actor = *current;
                if (actor == null || !IsPlayer(actor))
                    continue;

                if (IsDead(actor))
                {
                    var actorId = GetActorId(actor);
                    ActorPositions[actorId] = GetActorPosition(actor);
                    if (IsRaised(actor))
                        RezList[actorId] = 0;
                    if (!ActorNames.ContainsKey(actorId))
                        ActorNames.Add(actorId, GetActorName(actor));
                }
                else if (IsCastingResurrection(actor))
                {
                    var actorId = GetActorId(actor);
                    if (!ActorNames.ContainsKey(actorId))
                        ActorNames.Add(actorId, GetActorName(actor));

                    var corpseId = GetCastTarget(actor);
                    if (current == (byte**) _actorTablePtr)
                        PlayerRez = (corpseId, actorId);

                    if (!RezList.TryGetValue(corpseId, out var caster) || caster == PlayerRez.Item2)
                        RezList[corpseId] = actorId;
                }
            }
        }

        public void OnFrameworkUpdate(object _)
        {
            RezList.Clear();
            PlayerRez = (0, PlayerRez.Item2);
            IterateActors();
        }
    }
}
