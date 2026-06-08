using UnityEngine;
using UnityEngine.UI;

/// Conductor 2 — the inside conductor (toggle via RoleController). A billboard that rides the bus Cabin
/// and, when controlled, walks the cabin (WASD, clamped to the footprint).
///
/// HAGGLE mini-game: press Action (E) next to an aboard passenger to start arguing — the demand climbs
/// (Tk shown on screen). Press E again to lock it in and pocket that bonus. But each passenger has a
/// hidden patience; push past it and they refuse and you get nothing. Push your luck for more taka.
public class InsideConductor : MonoBehaviour
{
    public float moveSpeed = 3f;
    public float haggleRange = 1.6f;
    public float shoveRange = 1.8f;
    [Tooltip("Speed multiplier while squeezing past standing riders.")]
    public float crowdSlowFactor = 0.4f;
    public float crowdSlowRadius = 1.0f;

    [Header("Haggle")]
    public float demandStart = 5f;
    public float demandRate = 18f;          // taka/sec the demand climbs while arguing
    public int patienceMin = 10, patienceMax = 45;

    BillboardCharacter _view;
    bool _controlled;
    Vector3 _center, _half;

    // haggle state
    bool _haggling;
    Passenger _target;
    float _demand;
    int _patience;
    float _flashUntil;

    // ui
    GameObject _ui;
    Text _text;

    public void Setup(BillboardCharacter view, Transform cabin, Vector3 cabinCenter, Vector3 cabinSize)
    {
        _view = view;
        _center = cabinCenter;
        _half = cabinSize * 0.5f;
        transform.SetParent(cabin, false);
        transform.localPosition = _center;
        if (_view != null) _view.ApplyHeight();
        BuildUI();
    }

    public void SetControlled(bool on)
    {
        _controlled = on;
        if (!on)
        {
            EndHaggle();
            if (_ui != null) _ui.SetActive(false);
        }
    }

    void Update()
    {
        if (!_controlled) return;

        // Walk ONLY inside the configured aisle box; slow down when squeezing past standing riders.
        BusPassengers bp = BusPassengers.Instance;
        Vector3 wc = bp != null ? bp.WalkCenter : _center;
        float hw = bp != null ? bp.walkHalfWidth : 0.3f;
        float hl = bp != null ? bp.walkHalfLength : 3.0f;
        float spd = moveSpeed * (NearStanding() ? crowdSlowFactor : 1f);
        Vector2 mv = GameInput.Instance.move.ReadValue<Vector2>();
        Vector3 p = transform.localPosition;
        p.x += mv.x * spd * Time.deltaTime;
        p.z += mv.y * spd * Time.deltaTime;
        p.x = Mathf.Clamp(p.x, wc.x - hw, wc.x + hw);
        p.z = Mathf.Clamp(p.z, wc.z - hl, wc.z + hl);
        p.y = wc.y;
        transform.localPosition = p;

        // Haggle: first press starts it, second press locks it in.
        if (GameInput.Instance.action.WasPressedThisFrame())
        {
            if (!_haggling) StartHaggle();
            else AcceptHaggle();
        }

        // Shove a standing aisle passenger into a side seat (frees aisle space so more can board).
        if (GameInput.Instance.altAction.WasPressedThisFrame()) TryShove();

        if (_haggling)
        {
            _demand += demandRate * Time.deltaTime;
            if (_target == null || !_target.CanHaggle) EndHaggle();        // they left / already paid
            else if (_demand > _patience) RefuseHaggle();                  // pushed too far
        }

        // UI
        if (_text != null && _haggling) _text.text = "Haggling:  Tk " + Mathf.RoundToInt(_demand) + "     [E] take it";
        if (_ui != null) _ui.SetActive(_haggling || Time.time < _flashUntil);
    }

    void StartHaggle()
    {
        Passenger best = null;
        float bestSqr = haggleRange * haggleRange;
        foreach (Passenger p in FindObjectsByType<Passenger>(FindObjectsInactive.Exclude))
        {
            if (!p.CanHaggle) continue;
            float d = (p.transform.position - transform.position).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = p; }
        }
        if (best == null) return;
        _target = best;
        _haggling = true;
        _demand = demandStart;
        _patience = Random.Range(patienceMin, patienceMax + 1);
    }

    void TryShove()
    {
        Passenger best = null;
        float bestSqr = shoveRange * shoveRange;
        foreach (Passenger p in FindObjectsByType<Passenger>(FindObjectsInactive.Exclude))
        {
            if (!p.IsStanding) continue;
            float d = (p.transform.position - transform.position).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = p; }
        }
        if (best != null && BusPassengers.Instance != null && BusPassengers.Instance.ShovePassenger(best))
            Flash("Pushed back!");
    }

    bool NearStanding()
    {
        float r2 = crowdSlowRadius * crowdSlowRadius;
        foreach (Passenger p in FindObjectsByType<Passenger>(FindObjectsInactive.Exclude))
        {
            if (!p.IsStanding) continue;
            if ((p.transform.position - transform.position).sqrMagnitude < r2) return true;
        }
        return false;
    }

    void AcceptHaggle()
    {
        if (_target != null)
        {
            int bonus = Mathf.RoundToInt(_demand);
            if (ShiftManager.Instance != null) ShiftManager.Instance.AddEarnings(bonus);
            _target.Haggle();
            Flash("Paid  Tk " + bonus + " !");
        }
        EndHaggle();
    }

    void RefuseHaggle()
    {
        if (_target != null) _target.Haggle();   // done with them; no fare
        Flash("Refused!");
        EndHaggle();
    }

    void EndHaggle()
    {
        _haggling = false;
        _target = null;
    }

    void Flash(string msg)
    {
        if (_text != null) _text.text = msg;
        _flashUntil = Time.time + 1.2f;
    }

    void BuildUI()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Font.CreateDynamicFontFromOSFont("Arial", 16);

        GameObject canvasGO = new GameObject("HaggleUI");
        Canvas c = canvasGO.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        CanvasScaler sc = canvasGO.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1920, 1080);

        GameObject t = new GameObject("HaggleText");
        t.transform.SetParent(canvasGO.transform, false);
        _text = t.AddComponent<Text>();
        _text.font = font;
        _text.fontSize = 40;
        _text.alignment = TextAnchor.MiddleCenter;
        _text.color = new Color(1f, 0.9f, 0.3f);
        _text.horizontalOverflow = HorizontalWrapMode.Overflow;
        _text.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform rt = _text.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.28f);
        rt.anchorMax = new Vector2(0.5f, 0.28f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(900, 80);

        _ui = canvasGO;
        _ui.SetActive(false);
    }
}
