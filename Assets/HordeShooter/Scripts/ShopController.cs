using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Roguelite shop (M7c). When the game enters the shop phase (gamePhase == 2)
/// every player is warped to the shop space, three enhancement cards are
/// generated from their own gun's build state (per-player, local — no sync),
/// and interacting with a pedestal applies the card and reports READY to the
/// wave director. When the next wave starts everyone warps back to where they
/// were standing.
///
/// Card ids are shared with GunController (CARD_* constants).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ShopController : UdonSharpBehaviour
{
    public HordeNet hordeNet;
    public GunController gunTemplate;      // scene template (VRCPlayerObject) used to find MY gun copy
    public Transform shopSpawn;            // warp-in point inside the shop space
    public float teleportEyeCompensation = 1.4f;   // real clients treat the 2-arg TeleportTo pos as an EYE anchor

    public Text[] cardLabels;              // 3 floating labels (name + description; uGUI Text — TextMesh is not Udon-exposed)

    private GunController _myGun;
    private int[] _cardIds = new int[3];
    private int _lastPhase = -1;
    private Vector3 _returnPos;
    private Quaternion _returnRot;
    private bool _hasReturn;
    private bool _inShop;
    private bool _picked;

    void Update()
    {
        if (hordeNet == null) return;
        int phase = hordeNet.gamePhase;
        if (phase == _lastPhase) return;
        if (phase == 2) _EnterShop();
        else if (_inShop) _ExitShop();
        _lastPhase = phase;
    }

    // ================= shop flow =================

    private void _EnterShop()
    {
        _EnsureGun();
        VRCPlayerApi me = Networking.LocalPlayer;
        if (me != null && me.IsValid())
        {
            _returnPos = me.GetPosition();
            _returnRot = me.GetRotation();
            _hasReturn = true;
        }
        _inShop = true;
        _picked = false;
        _GenerateCards();
        if (shopSpawn != null) _TeleportTo(shopSpawn.position, shopSpawn.rotation);
    }

    private void _ExitShop()
    {
        _inShop = false;
        if (_hasReturn) _TeleportTo(_returnPos, _returnRot);
    }

    /// <summary>Resolves MY gun copy from the PlayerObject template (lazy: copies may not exist at Start).</summary>
    private void _EnsureGun()
    {
        if (_myGun != null) return;
        if (gunTemplate == null) return;
        VRCPlayerApi me = Networking.LocalPlayer;
        if (me == null || !me.IsValid()) return;
        GunController g = (GunController)me.FindComponentInPlayerObjects(gunTemplate);
        if (Utilities.IsValid(g)) _myGun = g;
    }

    /// <summary>Pedestal interact: apply the card, mark the shop visit done, report READY.</summary>
    public void _PickCard(int idx)
    {
        if (!_inShop || _picked) return;
        if (idx < 0 || idx >= 3) return;
        int id = _cardIds[idx];
        if (id < 0) return;
        _picked = true;
        _EnsureGun();
        if (_myGun != null) _myGun.ApplyCard(id);
        if (cardLabels != null)
        {
            for (int i = 0; i < cardLabels.Length && i < 3; i++)
            {
                if (cardLabels[i] == null) continue;
                cardLabels[i].text = (i == idx) ? "SELECTED - READY" : "";
            }
        }
        _ReportReady();
    }

    private void _ReportReady()
    {
        if (hordeNet == null) return;
        if (Networking.IsOwner(hordeNet.gameObject))
        {
            hordeNet._ReportShopReady();
        }
        else
        {
            hordeNet.SendCustomNetworkEvent(NetworkEventTarget.Owner, "_ReportShopReady");
        }
    }

    // ================= card generation =================

    private void _GenerateCards()
    {
        int[] opts = (_myGun != null) ? _myGun.GetCardOptions() : new int[0];
        int n = opts.Length;
        // Fisher-Yates shuffle so the three pedestals show distinct random picks
        for (int i = n - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            int t = opts[i]; opts[i] = opts[j]; opts[j] = t;
        }
        for (int i = 0; i < 3; i++)
        {
            _cardIds[i] = (i < n) ? opts[i] : -1;
            _UpdateCardLabel(i);
        }
    }

    private void _UpdateCardLabel(int i)
    {
        if (cardLabels == null || i >= cardLabels.Length || cardLabels[i] == null) return;
        int id = _cardIds[i];
        if (id < 0)
        {
            cardLabels[i].text = "(no offer)";
            return;
        }
        cardLabels[i].text = _CardName(id) + "\n" + _CardDesc(id) + "\n\n<touch to pick>";
    }

    private string _CardName(int id)
    {
        if (id == GunController.CARD_GATLING) return "GATLING";
        if (id == GunController.CARD_SHOTGUN) return "SHOTGUN";
        if (id == GunController.CARD_DRILL) return "DRILL";
        if (id == GunController.CARD_RAILGUN) return "RAILGUN";
        if (id == GunController.CARD_DAMAGE) return "DAMAGE UP";
        if (id == GunController.CARD_RATE) return "FIRE RATE UP";
        if (id == GunController.CARD_SPREAD) return "ACCURACY UP";
        if (id == GunController.CARD_MAG) return "MAG SIZE UP";
        if (id == GunController.CARD_RELOAD) return "RELOAD UP";
        if (id == GunController.CARD_BARREL_UP) return "BARREL LEVEL UP";
        return "?";
    }

    private string _CardDesc(int id)
    {
        if (id == GunController.CARD_GATLING) return "add barrel: high rate / low dmg";
        if (id == GunController.CARD_SHOTGUN) return "add barrel: 6-pellet blast";
        if (id == GunController.CARD_DRILL) return "add barrel: short range / huge dmg";
        if (id == GunController.CARD_RAILGUN) return "add barrel: sniper shot";
        if (id == GunController.CARD_DAMAGE) return "all barrels +25% damage";
        if (id == GunController.CARD_RATE) return "all barrels +20% fire rate";
        if (id == GunController.CARD_SPREAD) return "spread -30%";
        if (id == GunController.CARD_MAG) return "magazine +50%";
        if (id == GunController.CARD_RELOAD) return "reload time -30%";
        if (id == GunController.CARD_BARREL_UP) return "random owned barrel +1 level";
        return "";
    }

    // ================= debug =================

    public int debugPickIndex;

    /// <summary>Debug: pick the card at debugPickIndex (set via SetProgramVariable).</summary>
    public void _DebugPick()
    {
        _PickCard(debugPickIndex);
    }

    // ================= teleport =================
    private void _TeleportTo(Vector3 pos, Quaternion rot)
    {
        VRCPlayerApi me = Networking.LocalPlayer;
        if (me == null || !me.IsValid()) return;
        // Real clients interpret the 2-arg TeleportTo position as an EYE anchor
        // (measured: y=2 landed the feet at 0.599 = 2 - 1.401). Compensate so the
        // feet land at pos; tune teleportEyeCompensation after a real-client test.
        me.TeleportTo(pos + Vector3.up * teleportEyeCompensation, rot);
    }
}
