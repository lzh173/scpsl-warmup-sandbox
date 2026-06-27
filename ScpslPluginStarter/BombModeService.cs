using System;
using System.Collections.Generic;
using System.Linq;
using AdminToys;
using CustomPlayerEffects;
using InventorySystem.Items;
using InventorySystem.Items.Usables.Scp1576;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace ScpslPluginStarter;

internal sealed class BombModeService
{
    private const int DefaultRoundTimeSeconds = 105;
    private const int BombFuseTimeSeconds = 35;
    private const float BombPlantDurationSeconds = 20f;
    private const byte MovementLockIntensity = byte.MaxValue;
    private const float MovementLockDurationSeconds = 60f;

    private static readonly Vector3 BombHeldScale = new(0.5f, 0.5f, 0.5f);
    private static readonly Vector3 BombHeldLocalPosition = new(0f, 0.27f, -0.263f);
    private static readonly Quaternion BombHeldLocalRotation = new(-0.707106829f, 0f, 0f, 0.707106829f);
    private static readonly Vector3 BombPlantedOffset = new(0f, -1f, -0.75f);

    private Bounds _aSiteBounds;
    private Bounds _bSiteBounds;
    private InteractableToy? _defuseButton;
    private AdminToyBase? _bombVisual;

    public WarmupRoundMode Mode { get; private set; } = WarmupRoundMode.Standard;

    public BombRoundState State { get; private set; } = BombRoundState.Inactive;

    public int RoundToken { get; private set; }

    public int SecondsRemaining { get; private set; }

    public ushort BombSerial { get; private set; }

    public bool Enabled => Mode == WarmupRoundMode.Bomb;

    public bool RoundActive => Enabled && State is BombRoundState.PreRound or BombRoundState.Live or BombRoundState.Planted;

    public string BuildStatus()
    {
        return $"mode={Mode}, state={State}, secondsRemaining={SecondsRemaining}, bombSerial={BombSerial}";
    }

    public void SetMode(WarmupRoundMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        ResetRuntime();
    }

    public void ResetRuntime()
    {
        RoundToken++;
        State = Enabled ? BombRoundState.PreRound : BombRoundState.Inactive;
        SecondsRemaining = Enabled ? DefaultRoundTimeSeconds : 0;
        BombSerial = 0;
        _defuseButton = null;
        _bombVisual = null;
        _aSiteBounds = default;
        _bSiteBounds = default;
    }

    public bool TryStartRound(Dust2MapService mapService, IReadOnlyCollection<Player> participants, out string response)
    {
        ResetRuntime();
        if (!Enabled)
        {
            response = WarmupLocalization.T(
                "Bomb mode is disabled.",
                "炸弹模式已关闭。");
            return false;
        }

        if (!TryResolveMapObjects(mapService, out response))
        {
            State = BombRoundState.Inactive;
            return false;
        }

        Player? bombCarrier = SelectBombCarrier(participants);
        if (bombCarrier != null)
        {
            AssignBombTo(bombCarrier);
        }

        State = BombRoundState.Live;
        response = bombCarrier == null
            ? WarmupLocalization.T(
                "Bomb mode round started without an eligible terrorist bomb carrier.",
                "炸弹模式回合已开始，但没有合格的恐怖分子炸弹携带者。")
            : WarmupLocalization.T(
                $"Bomb mode round started. Bomb carrier: {bombCarrier.Nickname}.",
                $"炸弹模式回合已开始。炸弹携带者：{bombCarrier.Nickname}。");
        return true;
    }

    public BombRoundResult Tick(IReadOnlyCollection<Player> participants)
    {
        if (!RoundActive)
        {
            return BombRoundResult.None;
        }

        SecondsRemaining = Math.Max(0, SecondsRemaining - 1);
        int ctAlive = participants.Count(player => player.IsAlive && player.IsNTF);
        int tAlive = participants.Count(player => player.IsAlive && player.IsChaos);

        if (State == BombRoundState.Planted && SecondsRemaining == 0)
        {
            State = BombRoundState.Exploded;
            return BombRoundResult.Exploded;
        }

        if (State == BombRoundState.Defused)
        {
            return BombRoundResult.Defused;
        }

        if (ctAlive == 0 && tAlive == 0)
        {
            State = BombRoundState.Draw;
            return BombRoundResult.Draw;
        }

        if (ctAlive == 0)
        {
            State = BombRoundState.TerroristsWin;
            return BombRoundResult.TerroristsWin;
        }

        if (tAlive == 0 && State != BombRoundState.Planted)
        {
            State = BombRoundState.CounterTerroristsWin;
            return BombRoundResult.CounterTerroristsWin;
        }

        if (SecondsRemaining == 0)
        {
            State = BombRoundState.TimeExpired;
            return BombRoundResult.TimeExpired;
        }

        return BombRoundResult.None;
    }

    public string BuildHud(Player player, IReadOnlyCollection<Player> participants)
    {
        int ctAlive = participants.Count(p => p.IsAlive && p.IsNTF);
        int tAlive = participants.Count(p => p.IsAlive && p.IsChaos);
        string time = $"{SecondsRemaining / 60:00}:{SecondsRemaining % 60:00}";

        string task = State switch
        {
            BombRoundState.Planted when player.IsNTF => WarmupLocalization.T("Defuse the bomb.", "拆除炸弹。"),
            BombRoundState.Planted => WarmupLocalization.T("Protect the planted bomb.", "保护已安放的炸弹。"),
            _ when player.IsNTF => WarmupLocalization.T("Stop the terrorists or defuse if planted.", "阻止恐怖分子，如已安放则拆除炸弹。"),
            _ => WarmupLocalization.T("Plant the bomb or eliminate the counter-terrorists.", "安放炸弹或消灭反恐精英。"),
        };

        return $"<align=right><size=28>{WarmupLocalization.T("Bomb Mode", "炸弹模式")}\nCT {ctAlive} | T {tAlive}\n{time}\n{task}</size></align>";
    }

    public string DescribeResult(BombRoundResult result)
    {
        return result switch
        {
            BombRoundResult.Defused => WarmupLocalization.T("Counter-Terrorists win. The bomb was defused.", "反恐精英获胜。炸弹已被拆除。"),
            BombRoundResult.Exploded => WarmupLocalization.T("Terrorists win. The bomb exploded.", "恐怖分子获胜。炸弹已爆炸。"),
            BombRoundResult.CounterTerroristsWin => WarmupLocalization.T("Counter-Terrorists win. All terrorists were eliminated.", "反恐精英获胜。所有恐怖分子已被消灭。"),
            BombRoundResult.TerroristsWin => WarmupLocalization.T("Terrorists win. All counter-terrorists were eliminated.", "恐怖分子获胜。所有反恐精英已被消灭。"),
            BombRoundResult.TimeExpired => WarmupLocalization.T("Counter-Terrorists hold. Time expired before the bomb was planted.", "反恐精英获胜。时间已到，炸弹未被安放。"),
            BombRoundResult.Draw => WarmupLocalization.T("Bomb round ended in a draw.", "炸弹模式回合以平局结束。"),
            _ => string.Empty,
        };
    }

    public void HandleExplosionKill(IReadOnlyCollection<Player> participants)
    {
        foreach (Player player in participants.Where(player => player.IsAlive))
        {
            player.Kill(WarmupLocalization.T("The bomb exploded.", "炸弹已爆炸。"));
        }
    }

    public void OnUsingItem(PlayerUsingItemEventArgs ev)
    {
        if (!RoundActive || !IsBombUsable(ev.UsableItem))
        {
            return;
        }

        if (!ev.Player.IsChaos)
        {
            ev.IsAllowed = false;
            ResetBombCooldowns(ev.UsableItem);
            return;
        }

        if (!IsInsideBombSite(ev.Player.Position))
        {
            ev.IsAllowed = false;
            ResetBombCooldowns(ev.UsableItem);
            ev.Player.SendHint(WarmupLocalization.T("You must be inside a bomb site to plant.", "你必须位于炸弹安放区内才能安放。"), 3f);
            return;
        }

        ev.UsableItem.MaxCancellableDuration = BombPlantDurationSeconds;
        ApplyMovementLock(ev.Player);
    }

    public void OnUsedItem(PlayerUsedItemEventArgs ev)
    {
        if (!RoundActive || !IsBombUsable(ev.UsableItem) || !ev.Player.IsChaos)
        {
            return;
        }

        if (!IsInsideBombSite(ev.Player.Position))
        {
            return;
        }

        ResetBombCooldowns(ev.UsableItem);
        State = BombRoundState.Planted;
        SecondsRemaining = BombFuseTimeSeconds;
        if (_defuseButton != null)
        {
            _defuseButton.IsLocked = false;
        }
        DetachBombVisualToWorld(ev.Player.Position + BombPlantedOffset);
        ReleaseMovementLock(ev.Player);
        ev.Player.RemoveItem(ItemType.SCP1576, 1);
        ev.Player.SendHint(WarmupLocalization.T("Bomb planted.", "炸弹已安放。"), 3f);
    }

    public void OnCancelledUsingItem(PlayerCancelledUsingItemEventArgs ev)
    {
        if (!RoundActive || !IsBombUsable(ev.UsableItem))
        {
            return;
        }

        ResetBombCooldowns(ev.UsableItem);
        ReleaseMovementLock(ev.Player);
    }

    public void OnSearchingToy(PlayerSearchingToyEventArgs ev)
    {
        if (State != BombRoundState.Planted || !IsDefuseInteractable(ev.Interactable))
        {
            return;
        }

        if (ev.Player.IsChaos)
        {
            ev.IsAllowed = false;
            return;
        }

        ApplyMovementLock(ev.Player);
    }

    public void OnSearchedToy(PlayerSearchedToyEventArgs ev)
    {
        if (State != BombRoundState.Planted || !IsDefuseInteractable(ev.Interactable) || !ev.Player.IsNTF)
        {
            return;
        }

        State = BombRoundState.Defused;
        SecondsRemaining = 0;
        ReleaseMovementLock(ev.Player);
        HideBombVisual();
        ev.Player.SendHint(WarmupLocalization.T("Bomb defused.", "炸弹已拆除。"), 3f);
    }

    public void OnSearchToyAborted(PlayerSearchToyAbortedEventArgs ev)
    {
        if (!RoundActive || !IsDefuseInteractable(ev.Interactable))
        {
            return;
        }

        ReleaseMovementLock(ev.Player);
    }

    public void OnSearchingPickup(PlayerSearchingPickupEventArgs ev)
    {
        if (!RoundActive || !IsBombPickup(ev.Pickup))
        {
            return;
        }

        if (ev.Player.IsNTF)
        {
            ev.IsAllowed = false;
        }
    }

    public void OnPickingUpItem(PlayerPickingUpItemEventArgs ev)
    {
        if (!RoundActive || !IsBombPickup(ev.Pickup))
        {
            return;
        }

        BombSerial = ev.Pickup.Serial;
        AttachBombVisualToPlayer(ev.Player);
        ev.Player.SendHint(WarmupLocalization.T("You picked up the bomb.", "你捡起了炸弹。"), 3f);
    }

    public void OnDroppedItem(PlayerDroppedItemEventArgs ev)
    {
        if (!RoundActive || !IsBombPickup(ev.Pickup))
        {
            return;
        }

        BombSerial = ev.Pickup.Serial;
        ev.Throw = false;
        ev.Pickup.Rotation = Quaternion.identity;
        AttachBombVisualToPickup(ev.Pickup);
    }

    public void OnChangedItem(PlayerChangedItemEventArgs ev)
    {
        if (!RoundActive)
        {
            return;
        }

        if (ev.NewItem is UsableItem newUsable && IsBombUsable(newUsable))
        {
            ResetBombCooldowns(newUsable);
            ev.Player.SendHint(WarmupLocalization.T("Bomb equipped.", "炸弹已装备。"), 3f);
        }

        if (ev.OldItem is UsableItem oldUsable && IsBombUsable(oldUsable))
        {
            ResetBombCooldowns(oldUsable);
            ReleaseMovementLock(ev.Player);
        }
    }

    public void OnCuffing(PlayerCuffingEventArgs ev)
    {
        if (RoundActive)
        {
            ev.IsAllowed = false;
        }
    }

    private bool TryResolveMapObjects(Dust2MapService mapService, out string response)
    {
        if (!mapService.TryGetAdminToyComponent("Bomb", out AdminToyBase? bombVisual))
        {
            response = WarmupLocalization.T(
                "Dust2 Bomb admin toy could not be found.",
                "找不到 Dust2 炸弹管理玩具组件。");
            return false;
        }

        if (!mapService.TryGetAdminToyComponent("Bomb_Button", out InvisibleInteractableToy? buttonBase))
        {
            response = WarmupLocalization.T(
                "Dust2 Bomb_Button interactable could not be found.",
                "找不到 Dust2 Bomb_Button 交互组件。");
            return false;
        }

        if (!mapService.TryGetMarkerTransform("ASiteBounds", out Transform? aSiteTransform))
        {
            response = WarmupLocalization.T(
                "Dust2 ASiteBounds marker could not be found.",
                "找不到 Dust2 ASiteBounds 标记。");
            return false;
        }

        if (!mapService.TryGetMarkerTransform("BSiteBounds", out Transform? bSiteTransform))
        {
            response = WarmupLocalization.T(
                "Dust2 BSiteBounds marker could not be found.",
                "找不到 Dust2 BSiteBounds 标记。");
            return false;
        }

        _bombVisual = bombVisual!;
        _defuseButton = InteractableToy.Get(buttonBase!);
        _defuseButton.IsLocked = true;
        _aSiteBounds = new Bounds(aSiteTransform!.position, aSiteTransform.localScale);
        _bSiteBounds = new Bounds(bSiteTransform!.position, bSiteTransform.localScale);
        response = WarmupLocalization.T("Bomb map objects resolved.", "炸弹地图对象已解析。");
        return true;
    }

    private Player? SelectBombCarrier(IReadOnlyCollection<Player> participants)
    {
        Player? humanCarrier = participants
            .Where(player => player.IsAlive && player.IsChaos && !player.IsDummy)
            .OrderBy(_ => Guid.NewGuid())
            .FirstOrDefault();

        if (humanCarrier != null)
        {
            return humanCarrier;
        }

        return participants
            .Where(player => player.IsAlive && player.IsChaos)
            .Where(player => !player.IsDummy)
            .OrderBy(_ => Guid.NewGuid())
            .FirstOrDefault();
    }

    private void AssignBombTo(Player bombCarrier)
    {
        RemoveBombItemsFromEveryone();
        Item bombItem = bombCarrier.AddItem(ItemType.SCP1576, ItemAddReason.AdminCommand);
        BombSerial = bombItem.Serial;
        AttachBombVisualToPlayer(bombCarrier);
        bombCarrier.SendHint(WarmupLocalization.T("You have the bomb. Plant it in site A or B.", "你持有炸弹。请前往 A 区或 B 区安放。"), 4f);
    }

    private void RemoveBombItemsFromEveryone()
    {
        foreach (Player player in Player.List.Where(player => !player.IsDestroyed))
        {
            player.RemoveItem(ItemType.SCP1576, int.MaxValue);
        }
    }

    private bool IsInsideBombSite(Vector3 position)
    {
        return _aSiteBounds.Contains(position) || _bSiteBounds.Contains(position);
    }

    private bool IsDefuseInteractable(InteractableToy interactable)
    {
        return interactable != null && _defuseButton != null && ReferenceEquals(interactable.Base, _defuseButton.Base);
    }

    private bool IsBombUsable(UsableItem usableItem)
    {
        return usableItem != null
            && usableItem.Type == ItemType.SCP1576
            && (BombSerial == 0 || usableItem.Serial == BombSerial);
    }

    private bool IsBombPickup(Pickup pickup)
    {
        return pickup != null
            && pickup.Type == ItemType.SCP1576
            && (BombSerial == 0 || pickup.Serial == BombSerial);
    }

    private static void ResetBombCooldowns(UsableItem usableItem)
    {
        if (usableItem == null)
        {
            return;
        }

        usableItem.GlobalCooldownDuration = 0f;
        usableItem.PersonalCooldownDuration = 0f;
    }

    private void AttachBombVisualToPlayer(Player player)
    {
        if (_bombVisual?.gameObject == null || player?.GameObject == null)
        {
            return;
        }

        _bombVisual.gameObject.transform.localScale = BombHeldScale;
        _bombVisual.gameObject.transform.SetParent(player.GameObject.transform, false);
        _bombVisual.gameObject.transform.localPosition = BombHeldLocalPosition;
        _bombVisual.gameObject.transform.localRotation = BombHeldLocalRotation;
    }

    private void AttachBombVisualToPickup(Pickup pickup)
    {
        if (_bombVisual?.gameObject == null || pickup?.Transform == null)
        {
            return;
        }

        _bombVisual.gameObject.transform.SetParent(pickup.Transform, false);
        _bombVisual.gameObject.transform.localScale = BombHeldScale;
        _bombVisual.gameObject.transform.localPosition = Vector3.zero;
        _bombVisual.gameObject.transform.localRotation = Quaternion.identity;
    }

    private void DetachBombVisualToWorld(Vector3 position)
    {
        if (_bombVisual?.gameObject == null)
        {
            return;
        }

        Transform transform = _bombVisual.gameObject.transform;
        transform.SetParent(null, false);
        transform.position = position;
        transform.rotation = Quaternion.identity;
        transform.localScale = BombHeldScale;
    }

    private void HideBombVisual()
    {
        if (_bombVisual?.gameObject == null)
        {
            return;
        }

        _bombVisual.gameObject.transform.SetParent(null, false);
        _bombVisual.gameObject.transform.position = new Vector3(0f, -2000f, 0f);
    }

    private static void ApplyMovementLock(Player player)
    {
        if (player == null)
        {
            return;
        }

        ApplyEffect<Ensnared>(player);
        ApplyEffect<HeavyFooted>(player);
    }

    private static void ReleaseMovementLock(Player player)
    {
        if (player == null)
        {
            return;
        }

        ClearEffect<Ensnared>(player);
        ClearEffect<HeavyFooted>(player);
    }

    private static void ApplyEffect<TEffect>(Player player)
        where TEffect : StatusEffectBase
    {
        if (player.TryGetEffect<TEffect>(out TEffect effect))
        {
            player.EnableEffect(effect, MovementLockIntensity, MovementLockDurationSeconds, addDuration: false);
        }
    }

    private static void ClearEffect<TEffect>(Player player)
        where TEffect : StatusEffectBase
    {
        if (player.TryGetEffect<TEffect>(out TEffect effect))
        {
            player.DisableEffect(effect);
        }
    }
}

internal enum WarmupRoundMode
{
    None,
    Standard,
    Bomb,
}

internal enum BombRoundState
{
    Inactive,
    PreRound,
    Live,
    Planted,
    Defused,
    Exploded,
    CounterTerroristsWin,
    TerroristsWin,
    TimeExpired,
    Draw,
}

internal enum BombRoundResult
{
    None,
    Defused,
    Exploded,
    CounterTerroristsWin,
    TerroristsWin,
    TimeExpired,
    Draw,
}
