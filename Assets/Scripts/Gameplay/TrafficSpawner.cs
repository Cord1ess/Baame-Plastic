using System.Collections.Generic;
using UnityEngine;

/// Bus-relative traffic to dodge. Builds a small POOL of placeholder vehicles (code-generated coloured
/// boxes with trigger colliders + Obstacle) at load — no prefabs — and keeps them ahead of the bus,
/// recycling each one ahead again once the bus has driven past it (no mid-game Instantiate). Hitting
/// one sheds speed + damages bus health. Auto-finds the bus. (Phase C: wire to TiledRoadStreamer lanes.)
///
/// NOTE: these ride neither a chunk nor the bus (they're a self-managed pool), which is fine while
/// FloatingOrigin is off. When it's re-enabled, this pool must shift with the world (subscribe to the
/// origin delta) — flagged in PROJECT_UNDERSTANDING.
public class TrafficSpawner : MonoBehaviour
{
    [Header("Density / placement")]
    public int count = 8;
    public float spawnAheadMin = 30f;
    public float spawnAheadMax = 90f;
    [Tooltip("Sideways spread from the bus's heading line (tune to road width).")]
    public float lateralSpread = 5f;
    [Tooltip("Recycle a vehicle ahead once it's this far behind the bus.")]
    public float despawnBehind = 25f;

    [Header("Vehicle")]
    public Vector3 vehicleSize = new Vector3(2.2f, 1.6f, 4.5f);
    [Range(0f, 1f)] public float speedAfterHit = 0.5f;
    public int damageOnHit = 12;

    [Tooltip("Optional: road/ground layer so vehicles sit on it. If 0, they sit at the bus's height.")]
    public LayerMask groundMask;

    static readonly Color[] Palette =
    {
        new Color(0.8f,0.2f,0.2f), new Color(0.2f,0.35f,0.7f), new Color(0.85f,0.85f,0.2f),
        new Color(0.85f,0.85f,0.85f), new Color(0.2f,0.5f,0.3f), new Color(0.5f,0.5f,0.55f),
    };

    Transform _bus;
    readonly List<Transform> _pool = new List<Transform>();

    void Start()
    {
        _bus = FindBus();
        for (int i = 0; i < count; i++)
        {
            Transform v = BuildVehicle(i);
            _pool.Add(v);
            if (_bus != null) Reposition(v);
        }
    }

    Transform FindBus()
    {
        BusController bc = FindAnyObjectByType<BusController>();
        return bc != null ? bc.transform : null;
    }

    Transform BuildVehicle(int i)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Traffic_" + i;
        go.transform.SetParent(transform, false);
        go.transform.localScale = vehicleSize;

        Renderer r = go.GetComponent<Renderer>();
        if (r != null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh != null) { Material m = new Material(sh); m.SetColor("_BaseColor", Palette[i % Palette.Length]); r.material = m; }
        }

        BoxCollider col = go.GetComponent<BoxCollider>();
        if (col != null) col.isTrigger = true;

        Obstacle ob = go.AddComponent<Obstacle>();
        ob.speedAfterHit = speedAfterHit;
        ob.damageOnHit = damageOnHit;
        return go.transform;
    }

    void Update()
    {
        if (_bus == null) { _bus = FindBus(); return; }

        for (int i = 0; i < _pool.Count; i++)
        {
            Transform v = _pool[i];
            if (v == null) continue;
            if (Vector3.Dot(_bus.forward, v.position - _bus.position) < -despawnBehind)
                Reposition(v);
        }
    }

    void Reposition(Transform v)
    {
        Vector3 fwd = _bus.forward;
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        Vector3 pos = _bus.position
                    + fwd * Random.Range(spawnAheadMin, spawnAheadMax)
                    + right * Random.Range(-lateralSpread, lateralSpread);

        if (groundMask.value != 0 && Physics.Raycast(pos + Vector3.up * 10f, Vector3.down, out RaycastHit hit, 60f, groundMask))
            pos.y = hit.point.y + vehicleSize.y * 0.5f;
        else
            pos.y = _bus.position.y;   // roughly the bus's level so the trigger overlaps its path

        v.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd, Vector3.up));
        if (!v.gameObject.activeSelf) v.gameObject.SetActive(true);   // re-show any hideOnHit ones
    }
}
