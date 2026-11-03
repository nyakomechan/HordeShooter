using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// Local HUD: wave/phase text, alive count, boss HP bar (bossHp[]/bossSlots[]),
/// player HP bar (playerHp[]), horde ambient volume by nearest-enemy distance,
/// wave horn on transition into the wave phase, and down/revive handling
/// (down = black overlay + forced gun drop; revive = wave cleared by the team).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class HordeHUD : UdonSharpBehaviour
{
    public HordeNet hordeNet;
    public HordeSim hordeSim;
    public Text waveText;
    public Text quotaText;   // roguelite quota display (M7)
    public Text aliveText;
    public GameObject bossHpRoot;
    public Image bossHpFill;
    public Image playerHpFill;
    public GameObject downOverlay;
    public AudioSource audioSrc;
    public AudioSource oneShotSrc;   // horn etc. at fixed volume (ambient source volume follows proximity)
    public AudioClip waveHorn;
    public AudioClip hordeAmbient;

    private int _lastPhase = -1;
    private float _scanTimer;
    private int _lastAliveCount;
    private float _nearestDist = 1e9f;
    private bool _downed;

    private void Start()
    {
        if (audioSrc != null && hordeAmbient != null)
        {
            audioSrc.clip = hordeAmbient;
            audioSrc.loop = true;
            audioSrc.volume = 0f;
            audioSrc.Play();
        }
    }

    private void Update()
    {
        if (hordeNet == null) return;
        int phase = hordeNet.gamePhase;
        int wave = hordeNet.waveNumber;

        // horn on ANY transition into the wave phase (lobby→wave1 and intermission→waveN)
        if (_lastPhase != 1 && phase == 1 && oneShotSrc != null && waveHorn != null)
            oneShotSrc.PlayOneShot(waveHorn);
        _lastPhase = phase;

        _scanTimer += Time.deltaTime;
        if (_scanTimer >= 0.5f)
        {
            _scanTimer = 0f;
            _ScanSim();
        }

        if (waveText != null)
        {
            if (phase == 1) waveText.text = "WAVE " + wave;
            else if (phase == 2) waveText.text = "SHOP";
            else if (phase == 3) waveText.text = "GAME OVER - RESTARTING...";
            else waveText.text = "LOBBY - SHOP で GAME START";
        }
        if (quotaText != null)
        {
            if (phase == 1)
            {
                int qt = hordeNet.quotaType;
                int target = hordeNet.quotaTarget;
                int prog = hordeNet.quotaProgress;
                if (qt == 0) quotaText.text = "ノルマ: のこり " + Mathf.Max(0, target - prog) + "体";
                else if (qt == 1) quotaText.text = "ノルマ: あと " + Mathf.Max(0, target - prog) + "秒";
                else quotaText.text = "ノルマ: BOSSを倒せ";
            }
            else if (phase == 2) quotaText.text = "SHOP - 準備ができたら待機";
            else quotaText.text = "";
        }
        if (aliveText != null) aliveText.text = "" + _lastAliveCount;

        // player HP + down/revive handling
        VRCPlayerApi me = VRC.SDKBase.Networking.LocalPlayer;
        int idx = me.playerId - 1;
        if (idx < 0 || idx >= 16) idx = 0;
        float hp = hordeNet.playerHp[idx];
        if (playerHpFill != null) playerHpFill.fillAmount = Mathf.Clamp01(hp / 100f);
        if (hp <= 0f && !_downed)
        {
            // downed: lock locomotion (PlayerModSetter style), show the banner.
            // the gun locks itself (GunController.UpdateDownLock).
            _downed = true;
            me.SetWalkSpeed(0f);
            me.SetRunSpeed(0f);
            me.SetStrafeSpeed(0f);
            me.SetJumpImpulse(0f);
            if (downOverlay != null) downOverlay.SetActive(true);
        }
        else if (hp > 0f && _downed)
        {
            // revived (wave cleared by the team, or game-over auto reset).
            // positioning is handled by the ShopController: the lobby and the
            // shop are the same room now, so it warps the player there.
            _downed = false;
            me.SetWalkSpeed(2f);
            me.SetRunSpeed(4f);
            me.SetStrafeSpeed(2f);
            me.SetJumpImpulse(3f);
            if (downOverlay != null) downOverlay.SetActive(false);
        }

        // boss bar: first registered boss slot that is alive on the local mirror
        if (bossHpRoot != null)
        {
            float bossHp = -1f;
            int[] bs = hordeNet.bossSlots;
            int[] bh = hordeNet.bossHp;
            if (hordeSim != null && hordeSim.mirrorState != null)
            {
                for (int i = 0; i < 8; i++)
                {
                    int slot = bs[i];
                    if (slot < 0 || slot >= hordeSim.mirrorState.Length) continue;
                    int s = (int)hordeSim.mirrorState[slot];
                    if (s >= 1 && s <= 4) { bossHp = bh[i]; break; }
                }
            }
            bool show = bossHp >= 0f;
            if (bossHpRoot.activeSelf != show) bossHpRoot.SetActive(show);
            if (show && bossHpFill != null) bossHpFill.fillAmount = Mathf.Clamp01(bossHp / 400f);
        }

        // ambient volume by nearest-enemy distance (small floor so it stays audible)
        if (audioSrc != null && hordeAmbient != null)
            audioSrc.volume = 0.12f + Mathf.Clamp01(1f - _nearestDist / 25f) * 0.58f;
    }

    private void _ScanSim()
    {
        _lastAliveCount = 0;
        _nearestDist = 1e9f;
        if (hordeSim == null || hordeSim.mirrorState == null) return;
        float[] st = hordeSim.mirrorState;
        float[] px = hordeSim.mirrorPosX;
        float[] pz = hordeSim.mirrorPosZ;
        if (px == null || pz == null) return;
        Vector3 me = VRC.SDKBase.Networking.LocalPlayer.GetPosition();
        int alive = 0;
        float nearest = 1e9f;
        for (int i = 0; i < st.Length; i++)
        {
            int s = (int)st[i];
            if (s < 1 || s > 4) continue;
            alive++;
            float dx = px[i] - me.x;
            float dz = pz[i] - me.z;
            float d2 = dx * dx + dz * dz;
            if (d2 < nearest) nearest = d2;
        }
        _lastAliveCount = alive;
        _nearestDist = nearest > 1e8f ? 1e9f : Mathf.Sqrt(nearest);
    }
}
