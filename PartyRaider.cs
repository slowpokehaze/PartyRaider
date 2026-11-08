using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.IO;
using System.Threading;

using CommonBehaviors.Actions;
using PartyBot.IPC;
using Styx;
using Styx.Helpers;
using Styx.Logic;
using Styx.Logic.Combat;
using Styx.Logic.Inventory.Frames.LootFrame;
using Styx.Logic.Pathing;
using Styx.RemotableObjects;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using Styx.WoWInternals.World;
using TreeSharp;
using Action = TreeSharp.Action;
using Sequence = TreeSharp.Sequence;

namespace Styx.Bot.CustomBots
{
    /// <summary>
    /// PartyRaider role. Follower (default) = follow as usual.
    /// Leader = run the LeaderPlugin: hold position, fight if attacked, and
    /// broadcast our position/state to followers over TCP 127.0.0.1:1337.
    /// </summary>
    public enum PartyRaiderRole
    {
        Follower = 0,
        Leader = 1
    }

    /// <summary>
    /// Roll type used on group loot items (matches WoW RollOnLoot types):
    /// 0 = Give, 1 = Greed, 2 = Need.
    /// </summary>
    public enum LootRollType
    {
        Give = 0,
        Greed = 1,
        Need = 2
    }

    /// <summary>
    /// PartyRaider — a responsive "follow the group leader" bot with an optional
    /// defend-the-leader mode (it's a backup tank, not just a leash). It can also
    /// follow ANY player by name — a party member or one NOT in the party — and
    /// keeps up across adjacent-map borders by chasing the last known position.
    ///
    /// Behaviour:
    ///   * Re-evaluates EVERY game pulse (Root runs per-tick) — it starts moving the
    ///     instant the leader breaks away (no delayed "kick-in" like built-in Party).
    ///   * Follows the member with the GroupLeader flag (or the main tank if
    ///     FollowLeader = false). Reads the leader's live position each pulse and
    ///     falls back to the group-member info position when the object isn't loaded.
    ///   * Uses Flightor: walks when close, ground-mounts when medium, FLIES when the
    ///     leader is far — it will not freeze on a failed ground path across the map.
    ///   * Mounts INSTANTLY when the follow target mounts (MountLikeLeader): the
    ///     moment the leader gets on a mount (or a travel/flight form), the bot
    ///     mounts too — ground mount on old-world maps, flying mount in
    ///     Outland/Northrend when it has one — so it never chases on foot.
    ///   * No jumping while mounted (NoJumpWhenMounted): PartyRaider installs its
    ///     own stuck handler on the navigator; while on a mount, stuck recovery
    ///     strafes / dismounts / backs up instead of jumping. On foot it keeps
    ///     the original single jump when the way ahead is clear.
    ///   * DEFEND (DefendLeader = true): when the leader is in combat it fights
    ///     like LazyRaider — it runs the character's OWN combat routine (by
    ///     talents, not a fixed spell list). Target priority: (1) the hostile
    ///     attacking the protected target, (2) else the hostile attacking the
    ///     bot, (3) else the closest hostile in the radius. This is what makes
    ///     it a real backup tank instead of a follower that stands by.
    ///
    /// Settings: {AppPath}/Settings/PartyRaider_{char}.xml
    ///   FollowLeader     (bool)  true = follow the group leader, false = follow the main tank
    ///   FollowDistance   (int)   yards to keep from the target before stopping
    ///   UseMounts        (bool)  allow ground/flying mounts to keep up
    ///   StopInCombat     (bool)  hold position (do not follow) while you are in combat
    ///   DismountWhenClose(bool)  step off the mount once within range (follow mode)
    ///   DefendLeader     (bool)  fight in DEFEND mode: attack the enemies hitting the follow target / you
    ///   DefendRadius     (int)   yards around the follow target to look for attackers
    ///   DefendSpell      (string)legacy, unused — defense now runs the character's combat routine
    ///   FollowTarget     (string)follow this exact player name — party or non-party (empty = leader/tank mode)
    ///   MountLikeLeader  (bool)  mount up as soon as the follow target mounts (default true)
    ///   NoJumpWhenMounted(bool)  never jump while mounted — stuck recovery strafes / dismounts (default true)
    ///
    /// Install: this folder (Bots/PartyRaider/PartyRaider.cs) sits in your
    /// CopilotBuddy install; pick "PartyRaider" from the bot selector.
    /// </summary>
    public class PartyRaider : BotBase
    {
        private readonly Version _version = new Version(1, 5, 0);
        public override string Name
        {
            get { return "PartyRaider"; }
        }

        private PartyRaiderConfigForm _configForm;
        // Shown via the bot's config button (in the selector). A WinForms window
        // with all PartyRaider settings, including a follow-target / leader
        // dropdown (pick a specific paladin, the leader, or the main tank).
        public override System.Windows.Forms.Form ConfigurationForm
        {
            get
            {
                if (_configForm == null || _configForm.IsDisposed)
                    _configForm = new PartyRaiderConfigForm();
                return _configForm;
            }
        }

        // ------------------------------------------------------------------
        // World / group helpers
        // ------------------------------------------------------------------
        public static LocalPlayer Me
        {
            get { return StyxWoW.Me; }
        }

        public static bool IsInGroup
        {
            get
            {
                if (Me != null && (Me.IsInRaid || Me.IsInParty)) return true;
                // Fallback: Lua-based check, in case the memory offsets read stale.
                try { return WoWGroupInfo.Instance.IsInGroup; }
                catch { return false; }
            }
        }

        public static List<WoWPlayer> GroupMembers
        {
            get
            {
                if (!IsInGroup) return new List<WoWPlayer>();
                return Me.IsInRaid ? Me.RaidMembers : Me.PartyMembers;
            }
        }

        public static List<WoWPartyMember> GroupMemberInfos
        {
            get
            {
                if (!IsInGroup) return new List<WoWPartyMember>();
                // Use WoWGroupInfo (NOT LocalPlayer.*MemberInfos): the LocalPlayer variants
                // build members via the guid-constructor, so their Lua unit-id is "raid",
                // which breaks GroupLeader/IsMainTank/Name for PARTY members (no member is
                // ever the leader -> the bot finds no target and never follows).
                // WoWGroupInfo builds them via FromPartyIndex/FromRaidIndex (partyN/raidN).
                return WoWGroupInfo.Instance.AllMembers.ToList();
            }
        }

        private static bool IsGameStable()
        {
            return ObjectManager.IsInGame && Me != null && Me.IsValid;
        }

        // ------------------------------------------------------------------
        // Follow-target state. We track the target by Guid so following still
        // works when the leader's WoWPlayer object is not loaded (far away or on
        // another map).
        // ------------------------------------------------------------------
        private static ulong _targetGuid;
        private static string _targetName = "";
        private static ulong _lastLoggedTargetGuid;
        // Last known position of the target, refreshed while its object is loaded.
        // Lets us keep chasing a named player (party OR non-party) across an
        // adjacent-map boundary after they leave the current map.
        private static WoWPoint _lastKnownTargetPos = WoWPoint.Zero;

        // The loaded same-map WoWPlayer for the target (null when cross-map).
        public static WoWPlayer? Target { get; private set; }

        private static bool HasTarget
        {
            get { return _targetGuid != 0; }
        }

        // ------------------------------------------------------------------
        // Defend-target state (throttled world scan).
        // ------------------------------------------------------------------
        private static WoWUnit? _defendTarget;
        private static DateTime _lastDefendScan = DateTime.MinValue;
        private static readonly TimeSpan DefendScanInterval = TimeSpan.FromMilliseconds(200);

        // ------------------------------------------------------------------
        // Re-check bookkeeping
        // ------------------------------------------------------------------
        private static DateTime _lastTargetCheck = DateTime.MinValue;
        private static readonly TimeSpan TargetCheckInterval = TimeSpan.FromSeconds(1.0);
        private static bool needRecheck = true;   // forced by group-change events
        private static bool _botMovement;         // true while we issued a move ourselves

        private static bool ShouldRecheckTarget()
        {
            if (needRecheck) return true;
            return (DateTime.UtcNow - _lastTargetCheck) >= TargetCheckInterval;
        }

        // ------------------------------------------------------------------
        // Bot lifecycle
        // ------------------------------------------------------------------
        private Composite _root;

        public override Composite Root
        {
            get
            {
                return _root ?? (_root = new PrioritySelector(
                    DetectTargetBehavior(),
                    // No valid target -> stop and hold.
                    new Decorator(ret => !HasTarget,
                        new Action(ret => { StopMovement(); return RunStatus.Success; })),
                    // Combat first: defend the leader while it is being attacked,
                    // otherwise just follow it.
                    new PrioritySelector(
                        // DEFEND mode: run the character's own combat routine (by
                        // talents) against the attacker of the protected target / us.
                        new Decorator(ret => DefendModeActive,
                            CreateDefendBehavior()),
                        // LOOT: when not in combat and near a corpse/chest, loot it
                        // (with optional need/greed auto-roll). Placed before follow
                        // so a nearby corpse stops us instead of walking past it.
                        new Decorator(ret => ShouldLoot(),
                            new Action(ret => { DoLoot(); return RunStatus.Success; })),
                        // Mount instantly when the follow target mounts (before the
                        // distance-based mounting inside Flightor would kick in).
                        new Decorator(ret => !DefendModeActive && WantLeaderMount(),
                            new Action(ret => { MountUpLikeLeader(); return RunStatus.Success; })),
                        new Decorator(ret => ShouldFollow(),
                            new Action(ret => { MoveToLeader(); return RunStatus.Success; })),
                        // If we were bot-moving but no longer need to be, halt.
                        new Decorator(ret => Me != null && Me.IsMoving && _botMovement,
                            new Action(ret => { StopMovement(); return RunStatus.Success; })),
                        new ActionAlwaysSucceed()
                    )
                ));
            }
        }

        public override PulseFlags PulseFlags
        {
            // Plugins/BotEvents/Targeting so the character's combat routine runs
            // fully in DEFEND mode (same flags LazyRaider uses for combat).
            get { return PulseFlags.Objects | PulseFlags.Plugins | PulseFlags.BotEvents
                     | PulseFlags.InfoPanel | PulseFlags.Lua | PulseFlags.Targeting; }
        }

        // Called every game tick while the bot runs. In Leader role this pushes
        // our current position/state to followers over the LeaderPlugin TCP link.
        public override void Pulse()
        {
            base.Pulse();
            PushLeaderMessage();
        }

        public override void Initialize()
        {
            base.Initialize();
            Log(Color.Blue, "Version {0} initialized", _version);
        }

        public override void Start()
        {
            needRecheck = true;
            _lastTargetCheck = DateTime.MinValue;
            Target = null;
            _targetGuid = 0;
            _targetName = "";
            _botMovement = false;
            _lastLoggedTargetGuid = 0;
            _defendTarget = null;
            _lastDefendScan = DateTime.MinValue;

            Lua.Events.AttachEvent("PARTY_MEMBERS_CHANGED", HandleGroupChanged);
            Lua.Events.AttachEvent("PARTY_LEADER_CHANGED", HandleGroupChanged);
            Lua.Events.AttachEvent("RAID_UPDATE", HandleGroupChanged);
            Lua.Events.AttachEvent("START_LOOT_ROLL", OnStartLootRoll);

            EnsureFollowStuckHandler();
            SetupLeaderPlugin();

            Log(Color.Blue, "Version {0} started", _version);
        }

        public override void Stop()
        {
            Lua.Events.DetachEvent("PARTY_MEMBERS_CHANGED", HandleGroupChanged);
            Lua.Events.DetachEvent("PARTY_LEADER_CHANGED", HandleGroupChanged);
            Lua.Events.DetachEvent("RAID_UPDATE", HandleGroupChanged);
            Lua.Events.DetachEvent("START_LOOT_ROLL", OnStartLootRoll);

            TeardownLeaderPlugin();
            RestoreDefaultStuckHandler();
            StopMovement();
            Target = null;
            _targetGuid = 0;
            _defendTarget = null;
            _botMovement = false;
            Log(Color.Blue, "Version {0} stopped", _version);
        }

        private static void HandleGroupChanged(object sender, LuaEventArgs e)
        {
            needRecheck = true;
            _defendTarget = null;   // group changed: re-scan attackers
        }

        // ------------------------------------------------------------------
        // Behavior: keep Target up to date (always returns Failure so the
        // follow logic runs next).
        // ------------------------------------------------------------------
        private static Composite DetectTargetBehavior()
        {
            return new Action(ret =>
            {
                if (Me == null)
                    return RunStatus.Failure;

                // Keep the no-jump stuck handler installed (survives re-logins that
                // create a fresh navigator). Cheap reference compare per pulse.
                EnsureFollowStuckHandler();

                // Named-player mode (FollowTarget set) can follow a player who is
                // NOT in our party/raid, so don't require being in a group.
                bool namedMode = !string.IsNullOrWhiteSpace(PartyRaiderSettings.Instance.FollowTarget);
                // LeaderPlugin modes (Leader role, or follow-via-TCP) also work
                // without being in a group, so bypass the in-group requirement.
                bool leaderRole = PartyRaiderSettings.Instance.Role == PartyRaiderRole.Leader;
                bool tcpMode = PartyRaiderSettings.Instance.FollowViaLeaderPlugin;
                if (!namedMode && !leaderRole && !tcpMode && !IsInGroup)
                {
                    if (HasTarget)
                    {
                        ClearTarget();
                        Log("Left the group; holding position.");
                    }
                    needRecheck = false;
                    return RunStatus.Failure;
                }

                if (ShouldRecheckTarget())
                    DetectTarget();

                // Keep the last-known position fresh while the target is loaded,
                // so a named (possibly non-party) player can be chased across a
                // map boundary.
                if (Target != null && Target.IsValid && Target.Location.IsValid)
                    _lastKnownTargetPos = Target.Location;

                // Drop the loaded player if it died / was unloaded, but keep the
                // Guid so we can still navigate by the last known position.
                if (Target != null && !Target.IsValid)
                    Target = null;

                return RunStatus.Failure;
            });
        }

        private static void ClearTarget()
        {
            Target = null;
            _targetGuid = 0;
            _targetName = "";
            _defendTarget = null;
            _lastKnownTargetPos = WoWPoint.Zero;
            // NOTE: _lastLoggedTargetGuid is intentionally NOT reset here so the
            // "you are the target" message logs once, not on every re-check.
        }

        private static void DetectTarget()
        {
            _lastTargetCheck = DateTime.UtcNow;
            needRecheck = false;
            if (!IsGameStable()) return;

            // --- LEADER role: hold position, fight if attacked, broadcast state.
            //     We "follow" ourselves so DEFEND mode activates when we're hit and
            //     the (leader) position is always known to the followers.
            if (PartyRaiderSettings.Instance.Role == PartyRaiderRole.Leader)
            {
                if (Me == null) return;
                if (_targetGuid != Me.Guid)
                    SetTarget(Me.Guid, Me.Name, Me);
                return;
            }

            // --- MODE 0: LeaderPlugin (TCP) leader. Follow the leader whose
            //     position is streamed over 127.0.0.1:1337 (built-in PartyBot IPC).
            if (PartyRaiderSettings.Instance.FollowViaLeaderPlugin && _leaderMsg != null &&
                _leaderMsg.LeaderGuid != 0)
            {
                ulong lg = _leaderMsg.LeaderGuid;
                var live = ObjectManager.GetObjectByGuid<WoWPlayer>(lg);
                if (live == null || !live.IsValid)
                    live = FindPlayerByName(_leaderMsg.LeaderName);
                if (live != null && live.IsValid)
                {
                    if (live.Guid == Me.Guid)
                    {
                        ClearTarget();
                        Log("You are the LeaderPlugin leader; holding position.");
                        return;
                    }
                    SetTarget(live.Guid, live.Name, live);
                    return;
                }
                // Cross-map / not loaded: chase the live position from the TCP stream.
                var pos = new WoWPoint(_leaderMsg.LeaderX, _leaderMsg.LeaderY, _leaderMsg.LeaderZ);
                if (pos.IsValid)
                {
                    _lastKnownTargetPos = pos;
                    if (lg != _targetGuid)
                        SetTarget(lg,
                            string.IsNullOrEmpty(_leaderMsg.LeaderName) ? "LeaderPlugin leader" : _leaderMsg.LeaderName,
                            null);
                    return;
                }
            }

            // Pick the follow target.
            string followTarget = PartyRaiderSettings.Instance.FollowTarget;

            // --- MODE 1: explicit named player — a party member OR any other player. ---
            if (!string.IsNullOrWhiteSpace(followTarget))
            {
                // You are the named target -> hold position.
                if (Me != null && followTarget.Equals(Me.Name, StringComparison.OrdinalIgnoreCase))
                {
                    ClearTarget();
                    if (_lastLoggedTargetGuid != Me.Guid)
                    {
                        _lastLoggedTargetGuid = Me.Guid;
                        Log("You are the follow target ({0}); holding position.", followTarget);
                    }
                    return;
                }

                // (a) A live player with that name on THIS map (party or not).
                var live = FindPlayerByName(followTarget);
                if (live != null && live.IsValid)
                {
                    SetTarget(live.Guid, live.Name, live);
                    return;
                }

                // (b) A party/raid member with that name (cross-map; position comes
                //     from the group info).
                var info = GroupMemberInfos.FirstOrDefault(m => m != null &&
                    !string.IsNullOrEmpty(m.Name) &&
                    m.Name.Equals(followTarget, StringComparison.OrdinalIgnoreCase));
                if (info != null)
                {
                    SetTarget(info.Guid, info.Name,
                        ObjectManager.GetObjectByGuid<WoWPlayer>(info.Guid));
                    return;
                }

                // Named target not loaded right now. Keep the previous Guid + last
                // known position so we can still chase (e.g. across a map border).
                if (HasTarget)
                    Log("Named target '{0}' is not in sight; using last known position.", followTarget);
                else
                    Log("Named target '{0}' not found; holding position.", followTarget);
                return;
            }

            // --- MODE 2/3: group leader or main tank (original behavior). ---
            WoWPartyMember targetInfo;
            if (PartyRaiderSettings.Instance.FollowLeader)
            {
                targetInfo = GroupMemberInfos.FirstOrDefault(m => m != null && m.GroupLeader);
            }
            else
            {
                targetInfo = GroupMemberInfos.FirstOrDefault(m => m != null && m.IsMainTank)
                          ?? GroupMemberInfos.FirstOrDefault(m => m != null && m.IsOnline);
            }

            if (targetInfo == null)
            {
                if (HasTarget)
                    Log("No follow target found in group info; holding position.");
                ClearTarget();
                return;
            }

            // If I am the target, hold position.
            if (targetInfo.Guid == Me.Guid)
            {
                ClearTarget();
                if (_lastLoggedTargetGuid != targetInfo.Guid)
                {
                    _lastLoggedTargetGuid = targetInfo.Guid;
                    Log("You are the follow target ({0}); holding position.", targetInfo.Name);
                }
                return;
            }

            SetTarget(targetInfo.Guid, targetInfo.Name,
                ObjectManager.GetObjectByGuid<WoWPlayer>(targetInfo.Guid)
                ?? GroupMembers.FirstOrDefault(p => p != null && p.IsValid && p.Guid == targetInfo.Guid));
        }

        // Resolve a loaded player on the current map by (case-insensitive) name.
        // Works for ANY player — a party member or one that is NOT in the party —
        // as long as the client has the object loaded (within view distance).
        // If several players share the name, the closest one wins.
        private static WoWPlayer? FindPlayerByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Me == null)
                return null;

            WoWPlayer? best = null;
            double bestDist = double.MaxValue;
            foreach (var u in ObjectManager.CachedUnits)
            {
                if (!(u is WoWPlayer p)) continue;
                if (p == null || !p.IsValid) continue;
                if (p.Guid == Me.Guid) continue;   // never resolve to ourselves
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                double d = p.Distance;
                if (double.IsNaN(d) || !p.Location.IsValid) d = double.MaxValue;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }

        // Set the follow target from a resolved identity. `live` is the loaded
        // same-map player (null when cross-map / not yet loaded). We refresh the
        // last-known position and log once per target change.
        private static void SetTarget(ulong guid, string name, WoWPlayer? live)
        {
            _targetGuid = guid;
            _targetName = name;
            Target = (live != null && live.IsValid) ? live : null;

            if (Target != null && Target.Location.IsValid)
                _lastKnownTargetPos = Target.Location;

            if (_lastLoggedTargetGuid != _targetGuid)
            {
                _lastLoggedTargetGuid = _targetGuid;
                if (Target != null)
                    Log("Following {0}: {1} ({2})", TargetKindLabel(), _targetName, Target.Class);
                else
                    Log("Following {0}: {1} (cross-map, using last known position)", TargetKindLabel(), _targetName);
            }
        }

        private static string TargetKindLabel()
        {
            if (!string.IsNullOrWhiteSpace(PartyRaiderSettings.Instance.FollowTarget))
                return "player";
            return PartyRaiderSettings.Instance.FollowLeader ? "leader" : "tank";
        }

        // ------------------------------------------------------------------
        // DEFEND: attack the hostiles that are attacking the leader.
        // ------------------------------------------------------------------
        private static bool DefendModeActive
        {
            get
            {
                if (!PartyRaiderSettings.Instance.DefendLeader) return false;
                if (Me == null || Target == null || !Target.IsValid) return false;
                return Target.Combat;   // the leader is being attacked
            }
        }

        private static WoWUnit? FindDefendTarget()
        {
            if (Target == null || !Target.IsValid) return null;

            // Throttle: reuse the last target if it's still valid and fresh.
            if (_defendTarget != null && _defendTarget.IsValid &&
                (DateTime.UtcNow - _lastDefendScan) < DefendScanInterval)
                return _defendTarget;

            _lastDefendScan = DateTime.UtcNow;

            WoWPoint leaderPos = Target.Location;
            if (!leaderPos.IsValid)
            {
                _defendTarget = null;
                return null;
            }

            double radiusSqr =
                PartyRaiderSettings.Instance.DefendRadius * PartyRaiderSettings.Instance.DefendRadius;

            // Target priority (backup-tank logic):
            //   1) the hostile that is ATTACKING the protected target (the leader
            //      we defend) — closest wins.
            //   2) otherwise, the hostile attacking US (the bot itself) — closest.
            //   3) otherwise, the closest hostile in the radius (original fallback).
            WoWUnit? attackingLeader = null; double bestLeaderDist = double.MaxValue;
            WoWUnit? attackingMe     = null; double bestMeDist     = double.MaxValue;
            WoWUnit? nearest         = null; double nearestDist    = double.MaxValue;

            foreach (var u in ObjectManager.CachedUnits)
            {
                if (u == null || !u.IsValid) continue;
                if (Me != null && u.Guid == Me.Guid) continue;
                if (!u.IsHostile) continue;
                if (u.CurrentHealth <= 0) continue;
                WoWPoint loc = u.Location;
                if (!loc.IsValid) continue;
                double dSqr = leaderPos.DistanceSqr(loc);
                if (dSqr > radiusSqr) continue;

                // Fallback: closest hostile in the radius.
                if (dSqr < nearestDist) { nearestDist = dSqr; nearest = u; }

                // Which unit is this hostile focused on?
                var ct = u.CurrentTarget;
                if (ct == null || !ct.IsValid) continue;

                if (ct.Guid == Target.Guid)
                {
                    if (dSqr < bestLeaderDist) { bestLeaderDist = dSqr; attackingLeader = u; }
                }
                else if (Me != null && ct.Guid == Me.Guid)
                {
                    if (dSqr < bestMeDist) { bestMeDist = dSqr; attackingMe = u; }
                }
            }

            _defendTarget = attackingLeader ?? attackingMe ?? nearest;
            return _defendTarget;
        }

        // Positioning for DEFEND mode: find the defend target, set it as our
        // current target, close the gap and dismount. Returns TRUE while we're
        // still getting into position (the caller keeps holding); returns FALSE
        // once we're on foot, in range, and ready to fight (so the combat
        // routine runs).
        private static bool DefendPositioning()
        {
            if (Me == null || Target == null || !Target.IsValid) return true;

            WoWUnit? attacker = FindDefendTarget();
            if (attacker == null)
            {
                // Leader is in combat but no visible hostile in range yet — stick
                // right to the leader so we're in the fight.
                _botMovement = true;
                Flightor.MoveTo(Target.Location);
                return true;
            }

            // Always target the attacker. This drives auto-attack and the
            // routine's spell target (TankManager.FirstUnit ?? Me.CurrentTarget).
            attacker.Target();

            // Distance from us to the attacker.
            double dist = double.MaxValue;
            try
            {
                WoWPoint myLoc = Me.Location;
                WoWPoint attLoc = attacker.Location;
                if (myLoc.IsValid && attLoc.IsValid)
                    dist = Math.Sqrt(myLoc.DistanceSqr(attLoc));
            }
            catch { }

            // Close the gap: approach the attacker until we're right on top of it.
            const double engageDist = 4.0;
            if (double.IsNaN(dist) || dist > engageDist)
            {
                _botMovement = true;
                Flightor.MoveTo(attacker.Location);
                return true;
            }

            // In engage range: halt the approach, then get off the mount.
            if (Me.IsMoving)
            {
                WoWMovement.MoveStop();
                _botMovement = false;
                return true;
            }
            if (Me.Mounted && Flightor.MountHelper.Mounted)
            {
                Flightor.MountHelper.Dismount();
                return true;   // wait for the dismount, then fight next pulse
            }

            // On foot, in range, target set. Make sure auto-attack is on.
            if (!Me.IsAutoAttacking) Me.ToggleAttack();

            // Ready to fight -> let the combat routine run.
            return false;
        }

        // DEFEND-mode combat: runs the character's OWN combat routine (by talents
        // — the same mechanism LazyRaider uses) instead of a fixed spell list.
        // Targeting: the routine casts on TankManager.FirstUnit ?? Me.CurrentTarget;
        // we set Me.CurrentTarget to the attacker of the protected target (or of us),
        // so spells + auto-attack land on the right unit.
        private static Composite CreateDefendBehavior()
        {
            return new PrioritySelector(
                // 1) Get into position (see DefendPositioning). While moving /
                //    dismounting this returns Success (blocks the combat below).
                new Action(ret =>
                    DefendPositioning() ? RunStatus.Success : RunStatus.Failure),

                // 2) Not in combat yet: apply the routine's rest + pre-combat buffs.
                new Decorator(ret => IsGameStable() && !Me.Combat && Me.IsAlive,
                    new PrioritySelector(
                        new PrioritySelector(
                            new Decorator(ctx => RoutineManager.Current.RestBehavior != null,
                                RoutineManager.Current.RestBehavior),
                            new Decorator(ctx => RoutineManager.Current.NeedRest,
                                new Sequence(
                                    new Action(ret => { RoutineManager.Current.Rest(); return RunStatus.Success; })))),
                        new PrioritySelector(
                            new Decorator(ctx => RoutineManager.Current.PreCombatBuffBehavior != null,
                                RoutineManager.Current.PreCombatBuffBehavior),
                            new Decorator(ctx => RoutineManager.Current.NeedPreCombatBuffs,
                                new Sequence(
                                    new Action(ret => { RoutineManager.Current.PreCombatBuff(); return RunStatus.Success; }))))
                    )),

                // 3) In combat: the routine's heal + combat buffs + combat rotation.
                new Decorator(ret => IsGameStable() && Me.Combat,
                    new PrioritySelector(
                        new PrioritySelector(
                            new Decorator(ctx => RoutineManager.Current.HealBehavior != null,
                                new Sequence(RoutineManager.Current.HealBehavior,
                                    new Action(ret => RunStatus.Success))),
                            new Decorator(ctx => RoutineManager.Current.NeedHeal,
                                new Sequence(
                                    new Action(ret => { RoutineManager.Current.Heal(); return RunStatus.Success; })))),
                        new PrioritySelector(
                            new Decorator(ctx => RoutineManager.Current.CombatBuffBehavior != null,
                                new Sequence(RoutineManager.Current.CombatBuffBehavior,
                                    new Action(ret => RunStatus.Success))),
                            new Decorator(ctx => RoutineManager.Current.NeedCombatBuffs,
                                new Sequence(
                                    new Action(ret => { RoutineManager.Current.CombatBuff(); return RunStatus.Success; })))),
                        new PrioritySelector(
                            new Decorator(ctx => RoutineManager.Current.CombatBehavior != null,
                                new PrioritySelector(
                                    RoutineManager.Current.CombatBehavior,
                                    new Action(ret => RunStatus.Success))),
                            new Action(ret => { RoutineManager.Current.Combat(); return RunStatus.Success; }))
                    ))
            );
        }

        // ------------------------------------------------------------------
        // FOLLOW logic
        // ------------------------------------------------------------------
        private static bool ShouldFollow()
        {
            if (Me == null || !HasTarget) return false;
            if (Me.CurrentHealth <= 1) return false;

            if (PartyRaiderSettings.Instance.StopInCombat && Me.Combat) return false;

            // Same-map target: precise follow.
            if (Target != null && Target.IsValid)
            {
                if (Target.CurrentHealth <= 1) return false;   // leader dead
                if (!Target.InLineOfSightOCD) return true;     // out of sight -> go
                return Target.Distance > PartyRaiderSettings.Instance.FollowDistance;
            }

            // Cross-map / unloaded target: keep navigating toward the group
            // position (this will take flight paths for far destinations).
            return GetTargetDestination().IsValid;
        }

        private static void MoveToLeader()
        {
            WoWPoint dest = GetTargetDestination();
            if (!dest.IsValid) return;
            _botMovement = true;

            if (PartyRaiderSettings.Instance.UseMounts)
            {
                // Walk when close, ground-mount when medium, FLY when far.
                Flightor.MoveTo(dest);
            }
            else
            {
                // Ground navigation only (auto flight-paths for far destinations).
                Navigator.MoveTo(dest);
            }
        }

        private static WoWPoint GetTargetDestination()
        {
            // Prefer the live in-world position of the loaded target (same map).
            if (Target != null && Target.IsValid)
            {
                WoWPoint loc = Target.Location;
                if (loc.IsValid) return loc;
            }

            // LeaderPlugin (TCP) follower: the leader's streamed position is
            // authoritative and may be cross-map, so prefer it over the (possibly
            // unrelated) in-group leader position when the live player isn't loaded.
            if (PartyRaiderSettings.Instance.FollowViaLeaderPlugin && _lastKnownTargetPos.IsValid)
                return _lastKnownTargetPos;

            // Fallback: group member info position (UnitPosition) — works when
            // the target's object isn't loaded (far away or on another map).
            WoWPartyMember info = GroupMemberInfos.FirstOrDefault(m => m != null && m.Guid == _targetGuid);
            if (info == null)
                info = GroupMemberInfos.FirstOrDefault(m => m != null && (m.GroupLeader || m.IsMainTank));

            if (info != null && info.Location3D.IsValid)
                return info.Location3D;

            // Fallback: the last position where we actually saw the target — lets a
            // named (non-party) player be chased across an adjacent-map boundary.
            if (_lastKnownTargetPos.IsValid)
                return _lastKnownTargetPos;

            return WoWPoint.Zero;
        }

        private static void StopMovement()
        {
            if (Me == null || !Me.IsMoving) return;
            WoWMovement.MoveStop();
            _botMovement = false;

            // Step off the mount once we've stopped near the target.
            if (PartyRaiderSettings.Instance.DismountWhenClose && Me.Mounted &&
                Flightor.MountHelper.Mounted)
            {
                Flightor.MountHelper.Dismount();
            }
        }

        // ------------------------------------------------------------------
        // Mount up instantly when the follow target mounts
        // ------------------------------------------------------------------
        // The follow target is "on a mount" when it has the raw mount flag
        // (mount items / flying mounts) or is in a druid/shaman travel-style
        // form (travel / flight / epic flight).
        private static bool TargetIsMounted
        {
            get
            {
                if (Target == null || !Target.IsValid) return false;
                if (Target.Mounted) return true;
                ShapeshiftForm form = Target.Shapeshift;
                return form == ShapeshiftForm.Travel ||
                       form == ShapeshiftForm.FlightForm ||
                       form == ShapeshiftForm.EpicFlightForm;
            }
        }

        // True while we should be getting onto a mount to match the target.
        private static bool WantLeaderMount()
        {
            if (!PartyRaiderSettings.Instance.UseMounts) return false;
            if (!PartyRaiderSettings.Instance.MountLikeLeader) return false;
            if (Me == null || !Me.IsValid || !Me.IsAlive) return false;
            if (Me.Mounted || Me.Combat || Me.IsSwimming || Me.InVehicle) return false;
            if (!Me.IsOutdoors) return false;
            return TargetIsMounted;
        }

        // Mount up right now. Spell selection (ground vs flying) is the same as
        // the level bot's: ground mount in the old world, flying mount in
        // Outland/Northrend when available. Blocks the pulse until mounted —
        // Flightor does the same on its mount path.
        private static void MountUpLikeLeader()
        {
            if (Me == null) return;
            if (!Mount.CanMount()) return;
            Log("Follow target is mounted; mounting up now.");
            Mount.MountUp();
        }

        // ------------------------------------------------------------------
        // No-jump stuck handler. Installed on the navigator's stuck handler
        // while the bot runs: while on a mount, stuck recovery strafes /
        // dismounts / backs up instead of jumping (the default handler jumps
        // on the first stuck detection, which looks like constant bouncing on
        // a ground mount). On foot it keeps the original single jump when the
        // way ahead is clear.
        // ------------------------------------------------------------------
        private static StuckHandler _savedStuckHandler;
        private static CalmFollowStuckHandler _followStuckHandler;

        private static void EnsureFollowStuckHandler()
        {
            if (!PartyRaiderSettings.Instance.NoJumpWhenMounted) return;
            try
            {
                NavigationProvider provider = Navigator.NavigationProvider;
                if (provider == null) return;
                if (provider.StuckHandler == _followStuckHandler) return;
                if (_savedStuckHandler == null)
                    _savedStuckHandler = provider.StuckHandler;
                if (_followStuckHandler == null)
                    _followStuckHandler = new CalmFollowStuckHandler();
                provider.StuckHandler = _followStuckHandler;
                Log("No-jump stuck handler installed.");
            }
            catch (Exception e)
            {
                Log("Failed to install stuck handler: {0}", e.Message);
            }
        }

        private static void RestoreDefaultStuckHandler()
        {
            try
            {
                NavigationProvider provider = Navigator.NavigationProvider;
                if (provider != null && provider.StuckHandler == _followStuckHandler &&
                    _savedStuckHandler != null)
                {
                    provider.StuckHandler = _savedStuckHandler;
                    _savedStuckHandler = null;
                    Log("Default stuck handler restored.");
                }
            }
            catch { }
        }

        private sealed class CalmFollowStuckHandler : StuckHandler
        {
            private readonly System.Diagnostics.Stopwatch _stopwatch =
                new System.Diagnostics.Stopwatch();
            private WoWPoint _lastCheckLocation = WoWPoint.Empty;
            private WoWPoint _lastUnstickLocation = WoWPoint.Empty;
            private int _unstickStep;

            public CalmFollowStuckHandler()
            {
                _stopwatch.Start();
            }

            public override bool IsStuck()
            {
                var me = ObjectManager.Me;
                if (me == null || !me.IsValid) return false;
                if (me.Stunned || me.Fleeing || me.Dazed || me.Rooted)
                {
                    Reset();
                    return false;
                }

                // Only report stuck while WE are the one that was moving
                // (a deliberate stop is not a stuck state).
                WoWUnit? activeMover = WoWMovement.ActiveMover;
                if (activeMover == null || !activeMover.IsMe) return false;
                if (!activeMover.IsMoving) return false;

                if (_stopwatch.ElapsedMilliseconds < 500L) return false;

                WoWPoint loc = activeMover.Location;
                if (_lastCheckLocation != WoWPoint.Empty)
                {
                    float speed = activeMover.MovementInfo.IsSwimming
                        ? activeMover.MovementInfo.SwimSpeed
                        : activeMover.MovementInfo.RunSpeed;
                    float expected = speed * (float)_stopwatch.Elapsed.TotalSeconds * 0.6f;
                    if (expected > 0.5f && _lastCheckLocation.DistanceSqr(loc) < expected * expected)
                    {
                        _lastCheckLocation = loc;
                        return true;
                    }
                }

                _stopwatch.Restart();
                _lastCheckLocation = loc;
                return false;
            }

            public override void Unstick()
            {
                var me = ObjectManager.Me;
                if (me == null) return;

                WoWPoint loc = me.Location;
                // Moved far enough since the last unstick -> start the cycle fresh.
                if (_lastUnstickLocation != WoWPoint.Empty &&
                    _lastUnstickLocation.Distance2DSqr(loc) > 100f)
                    _unstickStep = 0;
                _lastUnstickLocation = loc;

                // On foot: the original single jump is allowed. Mounted: never.
                bool jumpingAllowed =
                    !PartyRaiderSettings.Instance.NoJumpWhenMounted || !me.Mounted;

                switch (_unstickStep % 4)
                {
                    case 0:
                        if (jumpingAllowed &&
                            GameWorld.IsInLineOfSight(loc.Add(0f, 0f, 2f),
                                                     loc.RayCast(me.Rotation, 2f).Add(0f, 0f, 2f)))
                        {
                            // Way ahead is clear: one jump (original on-foot behavior).
                            WoWMovement.Move(WoWMovement.MovementDirection.Forward |
                                             WoWMovement.MovementDirection.JumpAscend);
                            StyxWoW.Sleep(100);
                            WoWMovement.MoveStop(WoWMovement.MovementDirection.Forward |
                                                 WoWMovement.MovementDirection.JumpAscend);
                            StyxWoW.Sleep(200);
                            Log("Stuck on foot - jumped.");
                            _unstickStep++;
                            return;
                        }
                        // Mounted (or blocked ahead): strafe left instead of jumping.
                        Log(me.Mounted ? "Stuck while mounted - strafing left."
                                       : "Stuck on foot - strafing left.");
                        WoWMovement.Move(WoWMovement.MovementDirection.StrafeLeft);
                        StyxWoW.Sleep(500);
                        WoWMovement.MoveStop(WoWMovement.MovementDirection.StrafeLeft);
                        StyxWoW.Sleep(100);
                        _unstickStep++;
                        return;

                    case 1:
                        Log("Stuck - strafing right.");
                        WoWMovement.Move(WoWMovement.MovementDirection.StrafeRight);
                        StyxWoW.Sleep(500);
                        WoWMovement.MoveStop(WoWMovement.MovementDirection.StrafeRight);
                        StyxWoW.Sleep(100);
                        _unstickStep++;
                        return;

                    case 2:
                        if (me.Mounted)
                        {
                            // Give up the mount: go around the obstacle on foot.
                            Log("Stuck while mounted - dismounting to go around.");
                            Mount.Dismount("Stuck Handler");
                        }
                        else
                        {
                            Log("Stuck on foot - backing up.");
                            WoWMovement.Move(WoWMovement.MovementDirection.Backwards);
                            StyxWoW.Sleep(600);
                            WoWMovement.MoveStop(WoWMovement.MovementDirection.Backwards);
                            StyxWoW.Sleep(100);
                        }
                        _unstickStep++;
                        return;

                    default:
                        // End of the cycle: let the follow logic re-path, then restart.
                        _unstickStep = 0;
                        return;
                }
            }

            public override void Reset()
            {
                _stopwatch.Restart();
                _lastCheckLocation = WoWPoint.Empty;
                _unstickStep = 0;
            }
        }

        // ------------------------------------------------------------------
        // LOOT: find + loot nearby corpses/chests, with optional need/greed
        // auto-roll on group items.
        // ------------------------------------------------------------------
        private static readonly System.Collections.Generic.Dictionary<ulong, DateTime> _alreadyLooted =
            new System.Collections.Generic.Dictionary<ulong, DateTime>();
        private static DateTime _lastLootSlot = DateTime.MinValue;
        private const double LootInteractDistance = 5.0;

        private static bool IsRecentlyLooted(ulong guid)
        {
            DateTime t;
            if (_alreadyLooted.TryGetValue(guid, out t))
                return (DateTime.UtcNow - t) < TimeSpan.FromSeconds(60);
            return false;
        }

        private static void MarkLooted(ulong guid)
        {
            if (guid == 0) return;
            _alreadyLooted[guid] = DateTime.UtcNow;
            if (_alreadyLooted.Count > 200)
            {
                var now = DateTime.UtcNow;
                foreach (var g in _alreadyLooted.Keys.Where(k => (now - _alreadyLooted[k]) > TimeSpan.FromMinutes(10)).ToList())
                    _alreadyLooted.Remove(g);
            }
        }

        private static bool ShouldLoot()
        {
            var s = PartyRaiderSettings.Instance;
            if (!s.Loot) return false;
            if (Me == null || !Me.IsAlive) return false;
            if (Me.Combat) return false;                      // don't loot mid-fight
            if (LootTargeting.LootFrameIsOpen) return true;    // mid-loot: keep the node active
            return FindLootTarget() != null;
        }

        // Nearest lootable corpse (WoWUnit) or chest/node (WoWGameObject) within
        // LootRadius of us, skipping objects we already looted recently.
        private static WoWObject? FindLootTarget()
        {
            if (Me == null) return null;
            double radius = PartyRaiderSettings.Instance.LootRadius;
            WoWPoint myLoc = Me.Location;
            if (!myLoc.IsValid) return null;

            WoWObject? best = null; double bestDist = double.MaxValue;

            foreach (var u in ObjectManager.CachedUnits)
            {
                if (!(u is WoWUnit unit)) continue;
                if (unit == null || !unit.IsValid || unit == Me) continue;
                if (!unit.CanLoot) continue;
                if (IsRecentlyLooted(unit.Guid)) continue;
                WoWPoint loc = unit.Location;
                if (!loc.IsValid) continue;
                double d = myLoc.Distance(loc);
                if (d > radius) continue;
                if (d < bestDist) { bestDist = d; best = unit; }
            }

            foreach (var g in ObjectManager.CachedObjects)
            {
                if (!(g is WoWGameObject go)) continue;
                if (go == null || !go.IsValid) continue;
                if (!go.CanLoot) continue;
                if (IsRecentlyLooted(go.Guid)) continue;
                WoWPoint loc = go.Location;
                if (!loc.IsValid) continue;
                double d = myLoc.Distance(loc);
                if (d > radius) continue;
                if (d < bestDist) { bestDist = d; best = go; }
            }

            return best;
        }

        // Advance the loot state machine one step per pulse:
        //   * no window open  -> approach the target, then Interact() to open it
        //   * window open     -> take one slot at a time until empty, then close
        private static void DoLoot()
        {
            if (Me == null) return;

            if (LootTargeting.LootFrameIsOpen)
            {
                ulong lootingGuid = LootFrame.Instance.LootingObjectGuid;
                int items;
                try { items = LootFrame.Instance.LootItems; } catch { items = 0; }

                if (items > 0)
                {
                    // throttle so the loot list settles between slots
                    if ((DateTime.UtcNow - _lastLootSlot) < TimeSpan.FromMilliseconds(250))
                        return;
                    _lastLootSlot = DateTime.UtcNow;
                    try { LootFrame.Instance.Loot(0); }
                    catch (Exception e) { Log("Loot error: {0}", e.Message); }
                    return;
                }

                try { LootFrame.Instance.Close(); } catch { }
                MarkLooted(lootingGuid);
                Log("Loot complete.");
                return;
            }

            var target = FindLootTarget();
            if (target == null) return;
            WoWPoint tloc = target.Location;
            if (!tloc.IsValid) return;
            double d = Me.Location.Distance(tloc);
            if (double.IsNaN(d)) return;

            if (d > LootInteractDistance)
            {
                _botMovement = true;
                Navigator.MoveTo(tloc);
                return;
            }

            try { target.Interact(); }
            catch (Exception e) { Log("Interact error: {0}", e.Message); return; }
            Log("Looting nearby {0}.", target.GetType().Name);
        }

        // On a group-loot roll dialog: roll the configured type (Give/Greed/Need).
        private static void OnStartLootRoll(object sender, LuaEventArgs e)
        {
            var s = PartyRaiderSettings.Instance;
            if (!s.Loot || !s.AutoRollLoot) return;
            if (e == null || e.Args == null || e.Args.Length < 1) return;
            int type = (int)s.LootRollType;   // 0=Give 1=Greed 2=Need
            try
            {
                Lua.DoString("RollOnLoot(" + e.Args[0] + ", " + type +
                             ") ConfirmLootRoll(" + e.Args[0] + ", " + type + ")");
                Log("Rolled {0} on loot.", s.LootRollType);
            }
            catch (Exception ex)
            {
                Log("Roll error: {0}", ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // LeaderPlugin support. Reuses the built-in PartyBot.IPC protocol so
        // PartyRaider is fully compatible with the built-in Party Bot / DiscoBot:
        //   * Leader role  -> RemotingServer on 127.0.0.1:1337 streams our state.
        //   * Follower     -> RemotingClient follows the leader's streamed state.
        // ------------------------------------------------------------------
        private static RemotingServer _leaderServer;
        private static RemotingClient _followerClient;
        private static BotMessage _leaderMsg;
        private static bool _followerHooked;

        private static void SetupLeaderPlugin()
        {
            var s = PartyRaiderSettings.Instance;
            if (s.Role == PartyRaiderRole.Leader)
            {
                if (_leaderServer == null)
                {
                    try
                    {
                        _leaderServer = new RemotingServer();
                        Log("LeaderPlugin started (broadcasting on 127.0.0.1:1337).");
                    }
                    catch (Exception e)
                    {
                        Log("Failed to start LeaderPlugin: {0}", e.Message);
                        _leaderServer = null;
                    }
                }
                _followerClient = null;
            }
            else if (s.FollowViaLeaderPlugin)
            {
                if (_followerClient == null)
                {
                    try
                    {
                        _followerClient = new RemotingClient();
                        if (!_followerHooked)
                        {
                            _followerClient.ClientRecievedBotMessage += OnLeaderMessage;
                            _followerHooked = true;
                        }
                        Log("Connected to LeaderPlugin leader (127.0.0.1:1337).");
                    }
                    catch (Exception e)
                    {
                        Log("Cannot connect to LeaderPlugin leader: {0} Start the leader instance first, then click Start again.", e.Message);
                        _followerClient = null;
                    }
                }
            }
        }

        private static void TeardownLeaderPlugin()
        {
            if (_followerClient != null)
            {
                if (_followerHooked)
                {
                    _followerClient.ClientRecievedBotMessage -= OnLeaderMessage;
                    _followerHooked = false;
                }
                _followerClient = null;
            }
            // Note: the RemotingServer keeps its port for the process lifetime (same
            // as the built-in LeaderPlugin); it just stops receiving new state.
            _leaderMsg = null;
        }

        private static void OnLeaderMessage(BotMessage msg)
        {
            _leaderMsg = msg;
            if (msg != null)
            {
                var pos = new WoWPoint(msg.LeaderX, msg.LeaderY, msg.LeaderZ);
                if (pos.IsValid) _lastKnownTargetPos = pos;
                needRecheck = true;
            }
        }

        // Push our current state to followers (called from Pulse each tick).
        private static void PushLeaderMessage()
        {
            if (_leaderServer == null) return;
            var me = Me;
            if (me == null || !me.IsValid) return;
            if (me.Dead || me.IsGhost) return;
            WoWPoint loc = me.Location;
            if (!loc.IsValid) return;

            BotMessage msg;
            var cur = me.CurrentTarget;
            if (me.Combat && cur != null && cur.IsValid && !cur.Dead)
                msg = new BotMessage
                {
                    Message = "Kill", LeaderX = loc.X, LeaderY = loc.Y, LeaderZ = loc.Z,
                    TargetGuid = cur.Guid, LeaderGuid = me.Guid,
                    Timestamp = DateTime.Now, LeaderName = me.Name
                };
            else
                msg = new BotMessage
                {
                    Message = "FollowLeader", LeaderX = loc.X, LeaderY = loc.Y, LeaderZ = loc.Z,
                    LeaderGuid = me.Guid, Timestamp = DateTime.Now, LeaderName = me.Name
                };

            try { _leaderServer.SetMessage(msg); } catch { }
            try { StyxWoW.ResetAfk(); } catch { }
        }

        // ------------------------------------------------------------------
        // Logging
        // ------------------------------------------------------------------
        private static uint _lineCount;

        public static void Log(string msg, params object[] args)
        {
            Log(Color.Blue, msg, args);
        }

        public static void Log(Color clr, string msg, params object[] args)
        {
            try
            {
                Logging.Write(clr, "[PartyRaider] " + msg + (++_lineCount % 2 == 0 ? "" : " "), args);
            }
            catch (ThreadAbortException)
            {
                throw;
            }
            catch (Exception e)
            {
                Logging.Write(Color.Red, "[PartyRaider] Log exception: {0}", e.Message);
            }
        }
    }

    /// <summary>
    /// Per-character settings, saved to Settings/PartyRaider_{char}.xml.
    /// </summary>
    public sealed class PartyRaiderSettings : Settings
    {
        private static PartyRaiderSettings _singleton;
        public static PartyRaiderSettings Instance
        {
            get { return _singleton ?? (_singleton = new PartyRaiderSettings()); }
        }

        private PartyRaiderSettings()
            : base(Path.Combine(Logging.ApplicationPath,
                  string.Format("Settings/PartyRaider_{0}.xml", (StyxWoW.Me != null ? StyxWoW.Me.Name : "Unknown"))))
        {
        }

        [Setting("FollowLeader", "true = follow the group leader, false = follow the main tank")]
        [DefaultValue(true)]
        public bool FollowLeader { get; set; }

        [Setting("FollowDistance", "Yards to keep from the follow target before stopping")]
        [DefaultValue(6)]
        public int FollowDistance { get; set; }

        [Setting("UseMounts", "Allow ground/flying mounts to keep up with the target")]
        [DefaultValue(true)]
        public bool UseMounts { get; set; }

        [Setting("StopInCombat", "Hold position (do not follow) while you are in combat")]
        [DefaultValue(false)]
        public bool StopInCombat { get; set; }

        [Setting("DismountWhenClose", "Step off the mount once stopped within range (follow mode)")]
        [DefaultValue(true)]
        public bool DismountWhenClose { get; set; }

        [Setting("DefendLeader", "Attack the enemies that are attacking the follow target (backup tank)")]
        [DefaultValue(true)]
        public bool DefendLeader { get; set; }

        [Setting("DefendRadius", "Yards around the follow target to look for attackers")]
        [DefaultValue(15)]
        public int DefendRadius { get; set; }

        [Setting("DefendSpell", "Legacy / unused — DEFEND mode now runs the character's own combat routine (by talents) instead of a fixed spell")]
        [DefaultValue("Hammer of Justice")]
        public string DefendSpell { get; set; }

        [Setting("FollowTarget", "Follow this exact character name instead of the leader/tank. Works for ANY player in the world — a party member or one NOT in the party (empty = leader/tank mode). E.g. Pipyau")]
        [DefaultValue("")]
        public string FollowTarget { get; set; }

        [Setting("MountLikeLeader", "Mount up as soon as the follow target mounts (instantly, not after a distance threshold)")]
        [DefaultValue(true)]
        public bool MountLikeLeader { get; set; }

        [Setting("NoJumpWhenMounted", "Never jump while mounted: stuck recovery uses strafe / dismount / back-up instead of jumping")]
        [DefaultValue(true)]
        public bool NoJumpWhenMounted { get; set; }

        [Setting("Role", "Follower = follow as usual; Leader = run the LeaderPlugin (hold position, fight if attacked, broadcast position to followers over 127.0.0.1:1337)")]
        [DefaultValue(PartyRaiderRole.Follower)]
        public PartyRaiderRole Role { get; set; }

        [Setting("FollowViaLeaderPlugin", "Follow the LeaderPlugin leader (position streamed over TCP 127.0.0.1:1337) instead of the group leader")]
        [DefaultValue(false)]
        public bool FollowViaLeaderPlugin { get; set; }

        [Setting("Loot", "Loot corpses and chests near you (after combat)")]
        [DefaultValue(true)]
        public bool Loot { get; set; }

        [Setting("LootRadius", "Yards around you to look for lootable corpses/chests")]
        [DefaultValue(8)]
        public int LootRadius { get; set; }

        [Setting("AutoRollLoot", "Automatically roll on group loot items")]
        [DefaultValue(true)]
        public bool AutoRollLoot { get; set; }

        [Setting("LootRollType", "Roll type for group loot items: Give / Greed / Need")]
        [DefaultValue(LootRollType.Greed)]
        public LootRollType LootRollType { get; set; }
    }

    /// <summary>
    /// WinForms config window for PartyRaider (shown via the bot's config button).
    /// Built in code (no designer file) so it stays in this single .cs.
    /// </summary>
    public class PartyRaiderConfigForm : System.Windows.Forms.Form
    {
        private readonly System.Windows.Forms.ComboBox _cmbFollowTarget =
            new System.Windows.Forms.ComboBox { DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDown };
        private readonly System.Windows.Forms.NumericUpDown _nudFollowDistance =
            new System.Windows.Forms.NumericUpDown { Minimum = 1, Maximum = 200, Value = 6 };
        private readonly System.Windows.Forms.NumericUpDown _nudDefendRadius =
            new System.Windows.Forms.NumericUpDown { Minimum = 5, Maximum = 50, Value = 15 };
        private readonly System.Windows.Forms.CheckBox _chkUseMounts =
            new System.Windows.Forms.CheckBox { Text = "Use mounts (walk / ground / fly to keep up)" };
        private readonly System.Windows.Forms.CheckBox _chkStopInCombat =
            new System.Windows.Forms.CheckBox { Text = "Stop in combat (hold while you are fighting)" };
        private readonly System.Windows.Forms.CheckBox _chkDismountWhenClose =
            new System.Windows.Forms.CheckBox { Text = "Dismount when close" };
        private readonly System.Windows.Forms.CheckBox _chkDefendLeader =
            new System.Windows.Forms.CheckBox { Text = "Defend the follow target (backup tank)" };
        private readonly System.Windows.Forms.CheckBox _chkMountLikeLeader =
            new System.Windows.Forms.CheckBox { Text = "Mount up as soon as the follow target mounts" };
        private readonly System.Windows.Forms.CheckBox _chkNoJumpWhenMounted =
            new System.Windows.Forms.CheckBox { Text = "No jumping while mounted (strafe / dismount when stuck)" };
        private readonly System.Windows.Forms.CheckBox _chkActAsLeader =
            new System.Windows.Forms.CheckBox { Text = "Act as Leader (LeaderPlugin: hold + fight, broadcast position)" };
        private readonly System.Windows.Forms.CheckBox _chkFollowViaLeaderPlugin =
            new System.Windows.Forms.CheckBox { Text = "Follow the LeaderPlugin leader (TCP 127.0.0.1:1337)" };
        private readonly System.Windows.Forms.CheckBox _chkLoot =
            new System.Windows.Forms.CheckBox { Text = "Loot corpses & chests near you" };
        private readonly System.Windows.Forms.NumericUpDown _nudLootRadius =
            new System.Windows.Forms.NumericUpDown { Minimum = 2, Maximum = 50, Value = 8 };
        private readonly System.Windows.Forms.CheckBox _chkAutoRollLoot =
            new System.Windows.Forms.CheckBox { Text = "Auto-roll group loot items" };
        private readonly System.Windows.Forms.ComboBox _cmbLootRollType =
            new System.Windows.Forms.ComboBox { DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
        private readonly System.Windows.Forms.Label _lblStatus =
            new System.Windows.Forms.Label { AutoSize = true, ForeColor = System.Drawing.Color.Green };

        public PartyRaiderConfigForm()
        {
            Text = "PartyRaider - Settings";
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            ClientSize = new System.Drawing.Size(380, 1300);
            MinimumSize = new System.Drawing.Size(340, 560);

            var table = new System.Windows.Forms.TableLayoutPanel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                ColumnCount = 1,
                AutoScroll = true,
                Padding = new System.Windows.Forms.Padding(12)
            };
            table.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100f));

            int row = 0;
            AddLabelRow(table, ref row, "Leader to follow:");
            AddControlRow(table, ref row, _cmbFollowTarget, 28);
            var hint = new System.Windows.Forms.Label
            {
                Text = "Pick a group member, or type ANY player's name to follow them (party or not).",
                AutoSize = true,
                ForeColor = System.Drawing.Color.Gray,
                Dock = System.Windows.Forms.DockStyle.Top
            };
            table.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            table.Controls.Add(hint, 0, row); row++;
            AddLabelRow(table, ref row, "Follow distance (yards):");
            AddControlRow(table, ref row, _nudFollowDistance, 26);
            AddControlRow(table, ref row, _chkUseMounts, 22);
            AddControlRow(table, ref row, _chkStopInCombat, 22);
            AddControlRow(table, ref row, _chkDismountWhenClose, 22);
            AddControlRow(table, ref row, _chkDefendLeader, 22);
            AddControlRow(table, ref row, _chkMountLikeLeader, 22);
            AddControlRow(table, ref row, _chkNoJumpWhenMounted, 22);
            AddLabelRow(table, ref row, "Defend radius (yards):");
            AddControlRow(table, ref row, _nudDefendRadius, 26);
            AddLabelRow(table, ref row, "Defense: runs your character's own combat routine");
            AddLabelRow(table, ref row, "(by talents); hits whoever attacks the target / you");

            AddControlRow(table, ref row, _chkActAsLeader, 22);
            AddControlRow(table, ref row, _chkFollowViaLeaderPlugin, 22);
            AddLabelRow(table, ref row, "Looting:");
            AddControlRow(table, ref row, _chkLoot, 22);
            AddLabelRow(table, ref row, "Loot radius (yards):");
            AddControlRow(table, ref row, _nudLootRadius, 26);
            AddControlRow(table, ref row, _chkAutoRollLoot, 22);
            AddLabelRow(table, ref row, "Loot roll type:");
            AddControlRow(table, ref row, _cmbLootRollType, 26);

            table.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 36));
            var btnSave = new System.Windows.Forms.Button
            {
                Text = "Save & Close",
                Dock = System.Windows.Forms.DockStyle.Left,
                Width = 150,
                Height = 30,
                Margin = new System.Windows.Forms.Padding(0, 2, 0, 0)
            };
            btnSave.Click += (s, e) => SaveAndClose();
            table.Controls.Add(btnSave, 0, row); row++;

            table.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20));
            _lblStatus.Dock = System.Windows.Forms.DockStyle.Left;
            table.Controls.Add(_lblStatus, 0, row);

            Controls.Add(table);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _cmbLootRollType.Items.Add("Give");
            _cmbLootRollType.Items.Add("Greed");
            _cmbLootRollType.Items.Add("Need");
            _cmbLootRollType.SelectedIndex = 0;
            PopulateFollowTargets();
            LoadSettings();
        }

        private static void AddLabelRow(System.Windows.Forms.TableLayoutPanel table, ref int row, string text)
        {
            table.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            table.Controls.Add(new System.Windows.Forms.Label { Text = text, Dock = System.Windows.Forms.DockStyle.Top, AutoSize = true }, 0, row);
            row++;
        }

        private static void AddControlRow(System.Windows.Forms.TableLayoutPanel table, ref int row, System.Windows.Forms.Control control, int height)
        {
            // 2x vertical spacing (applied twice): quadruple the row height, and
            // keep the control at its natural size (Dock=Top) so it doesn't
            // stretch to fill the gap.
            table.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, height * 4));
            control.Dock = System.Windows.Forms.DockStyle.Top;
            table.Controls.Add(control, 0, row);
            row++;
        }

        // Fill the leader dropdown: the two default modes, the currently saved
        // leader (so it stays visible/selectable even when NOT in a group), and
        // every current group member (by name) — all deduplicated.
        private void PopulateFollowTargets()
        {
            _cmbFollowTarget.Items.Clear();
            _cmbFollowTarget.Items.Add("Group Leader (default)");
            _cmbFollowTarget.Items.Add("Main Tank (default)");

            try
            {
                string saved = PartyRaiderSettings.Instance.FollowTarget;
                if (!string.IsNullOrWhiteSpace(saved) &&
                    !ComboContains(_cmbFollowTarget, saved, StringComparison.OrdinalIgnoreCase))
                    _cmbFollowTarget.Items.Add(saved);
            }
            catch { /* settings not ready yet */ }

            try
            {
                foreach (var m in PartyRaider.GroupMemberInfos)
                    if (m != null && !string.IsNullOrEmpty(m.Name) &&
                        !ComboContains(_cmbFollowTarget, m.Name, StringComparison.OrdinalIgnoreCase))
                        _cmbFollowTarget.Items.Add(m.Name);
            }
            catch { /* not in a group yet */ }

            if (_cmbFollowTarget.Items.Count > 0)
                _cmbFollowTarget.SelectedIndex = 0;
        }

        private static bool ComboContains(System.Windows.Forms.ComboBox cmb, string val, StringComparison cmp)
        {
            foreach (var it in cmb.Items)
                if (string.Equals(it as string, val, cmp)) return true;
            return false;
        }

        private void LoadSettings()
        {
            var s = PartyRaiderSettings.Instance;
            if (!string.IsNullOrWhiteSpace(s.FollowTarget))
            {
                // Show the saved leader even if it is not in the current list.
                if (!ComboContains(_cmbFollowTarget, s.FollowTarget, StringComparison.OrdinalIgnoreCase))
                    _cmbFollowTarget.Items.Add(s.FollowTarget);
                _cmbFollowTarget.Text = s.FollowTarget;
            }
            else
            {
                _cmbFollowTarget.SelectedIndex = s.FollowLeader ? 0 : 1;
            }
            _nudFollowDistance.Value = Clamp(s.FollowDistance, (int)_nudFollowDistance.Minimum, (int)_nudFollowDistance.Maximum);
            _nudDefendRadius.Value = Clamp(s.DefendRadius, (int)_nudDefendRadius.Minimum, (int)_nudDefendRadius.Maximum);
            _chkUseMounts.Checked = s.UseMounts;
            _chkStopInCombat.Checked = s.StopInCombat;
            _chkDismountWhenClose.Checked = s.DismountWhenClose;
            _chkDefendLeader.Checked = s.DefendLeader;
            _chkMountLikeLeader.Checked = s.MountLikeLeader;
            _chkNoJumpWhenMounted.Checked = s.NoJumpWhenMounted;
            _chkActAsLeader.Checked = s.Role == PartyRaiderRole.Leader;
            _chkFollowViaLeaderPlugin.Checked = s.FollowViaLeaderPlugin;
            _chkLoot.Checked = s.Loot;
            _nudLootRadius.Value = Clamp(s.LootRadius, (int)_nudLootRadius.Minimum, (int)_nudLootRadius.Maximum);
            _chkAutoRollLoot.Checked = s.AutoRollLoot;
            _cmbLootRollType.SelectedItem = s.LootRollType.ToString();
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        private void SaveAndClose()
        {
            try
            {
                var s = PartyRaiderSettings.Instance;
                // Read the text (not SelectedItem) so typed-in names are kept too.
                string sel = (_cmbFollowTarget.Text ?? "").Trim();
                if (sel.Length == 0 || sel == "Group Leader (default)")
                {
                    s.FollowTarget = "";
                    s.FollowLeader = true;
                }
                else if (sel == "Main Tank (default)")
                {
                    s.FollowTarget = "";
                    s.FollowLeader = false;
                }
                else
                {
                    s.FollowTarget = sel;
                }
                s.FollowDistance = (int)_nudFollowDistance.Value;
                s.DefendRadius = (int)_nudDefendRadius.Value;
                s.UseMounts = _chkUseMounts.Checked;
                s.StopInCombat = _chkStopInCombat.Checked;
                s.DismountWhenClose = _chkDismountWhenClose.Checked;
                s.DefendLeader = _chkDefendLeader.Checked;
                s.MountLikeLeader = _chkMountLikeLeader.Checked;
                s.NoJumpWhenMounted = _chkNoJumpWhenMounted.Checked;
                s.Role = _chkActAsLeader.Checked ? PartyRaiderRole.Leader : PartyRaiderRole.Follower;
                s.FollowViaLeaderPlugin = _chkFollowViaLeaderPlugin.Checked;
                s.Loot = _chkLoot.Checked;
                s.LootRadius = (int)_nudLootRadius.Value;
                s.AutoRollLoot = _chkAutoRollLoot.Checked;
                if (Enum.TryParse<LootRollType>(_cmbLootRollType.SelectedItem as string, out var rt))
                    s.LootRollType = rt;
                s.Save();
                _lblStatus.Text = "Saved.";
                PartyRaider.Log("Settings saved: followTarget='{0}' followLeader={1}", s.FollowTarget, s.FollowLeader);
                Close();
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Error: " + ex.Message;
            }
        }
    }
}
