using UnityEngine;
using UnityEngine.UI;

/// Bottom-right speed readout: big km/h number, current gear, and an RPM bar that fills within each gear
/// and snaps back on every upshift (driven by BusController.Rpm01) — so the auto-gearbox is visible.
/// Auto-spawns; reads BusController.Instance. No scene setup.
public class SpeedometerHud : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<SpeedometerHud>() == null)
            new GameObject("SpeedometerHud").AddComponent<SpeedometerHud>();
    }

    BusController _bus;
    Text _speed, _gear;
    RectTransform _rpmFill;
    Image _rpmImg;
    float _rpmMaxWidth = 220f;

    void Start() { BuildUI(); }

    void Update()
    {
        if (_bus == null) _bus = BusController.Instance;
        if (_bus == null) return;

        _speed.text = Mathf.RoundToInt(_bus.SpeedKmh).ToString();
        _gear.text = "GEAR " + _bus.Gear;

        float rpm = _bus.Rpm01;
        _rpmFill.sizeDelta = new Vector2(_rpmMaxWidth * Mathf.Clamp01(rpm), _rpmFill.sizeDelta.y);
        _rpmImg.color = rpm > 0.85f ? new Color(1f, 0.3f, 0.2f) : new Color(0.3f, 0.85f, 1f);
    }

    void BuildUI()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        GameObject canvasGO = new GameObject("SpeedoCanvas");
        canvasGO.transform.SetParent(transform, false);
        Canvas c = canvasGO.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        CanvasScaler sc = canvasGO.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1920, 1080);

        // Container anchored bottom-right
        GameObject panel = new GameObject("Panel");
        panel.transform.SetParent(canvasGO.transform, false);
        RectTransform pr = panel.AddComponent<RectTransform>();
        pr.anchorMin = pr.anchorMax = new Vector2(1f, 0f);
        pr.pivot = new Vector2(1f, 0f);
        pr.anchoredPosition = new Vector2(-40f, 40f);
        pr.sizeDelta = new Vector2(260f, 160f);

        _speed = MakeText(panel.transform, font, 92, TextAnchor.LowerRight, new Color(1f, 1f, 1f),
                          new Vector2(0f, 50f), new Vector2(260f, 100f), "0");
        MakeText(panel.transform, font, 26, TextAnchor.LowerRight, new Color(0.8f, 0.85f, 0.9f),
                 new Vector2(0f, 30f), new Vector2(260f, 30f), "km/h");
        _gear = MakeText(panel.transform, font, 26, TextAnchor.LowerRight, new Color(1f, 0.9f, 0.4f),
                         new Vector2(0f, 6f), new Vector2(260f, 30f), "GEAR 1");

        // RPM bar background + fill (right-aligned, grows leftward via pivot)
        GameObject bg = new GameObject("RpmBg");
        bg.transform.SetParent(panel.transform, false);
        Image bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.45f);
        RectTransform bgr = bg.GetComponent<RectTransform>();
        bgr.anchorMin = bgr.anchorMax = new Vector2(1f, 0f);
        bgr.pivot = new Vector2(1f, 0f);
        bgr.anchoredPosition = new Vector2(0f, -4f);
        bgr.sizeDelta = new Vector2(_rpmMaxWidth, 10f);

        GameObject fill = new GameObject("RpmFill");
        fill.transform.SetParent(bg.transform, false);
        _rpmImg = fill.AddComponent<Image>();
        _rpmImg.color = new Color(0.3f, 0.85f, 1f);
        _rpmFill = fill.GetComponent<RectTransform>();
        _rpmFill.anchorMin = _rpmFill.anchorMax = new Vector2(1f, 0f);
        _rpmFill.pivot = new Vector2(1f, 0f);
        _rpmFill.anchoredPosition = Vector2.zero;
        _rpmFill.sizeDelta = new Vector2(0f, 10f);
    }

    Text MakeText(Transform parent, Font font, int size, TextAnchor anchor, Color col,
                  Vector2 pos, Vector2 sz, string initial)
    {
        GameObject go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        Text t = go.AddComponent<Text>();
        t.font = font; t.fontSize = size; t.alignment = anchor; t.color = col; t.text = initial;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = sz;
        return t;
    }
}
