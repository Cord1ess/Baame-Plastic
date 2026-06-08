using UnityEngine;
using UnityEngine.SceneManagement;

/// Keeps the world near the origin so the endless road never drives the bus into far-from-origin float
/// imprecision (jitter, unstable sphere physics, shimmering seams). When the BUS passes `threshold`
/// metres from origin (horizontally), every root object — road, stops, traffic, the detached physics
/// sphere — is shifted by -busPos so the bus snaps back near 0,0,0. Velocities are unaffected by a
/// position shift, so motion is seamless; the shift is invisible because the whole world moves together.
///
/// Spline-safe and chunk-independent: it notifies systems that cache WORLD positions (the road
/// generator's walk cursors, the stop spawner's distance anchors) so they shift too. Attach anywhere
/// (camera is fine); it finds the bus itself.
public class FloatingOrigin : MonoBehaviour
{
    [Tooltip("Recenter once the bus is this far (horizontal) from world origin. ~1km is well within " +
             "float precision, so there's never visible degradation and recenters stay rare.")]
    public float threshold = 1000f;

    [Tooltip("Master switch. On by default now that the recenter is physics-safe and spline-aware.")]
    public bool recenter = true;

    Transform _bus;
    BusController _bc;
    TiledRoadStreamer _tiled;         // the pooled-tile road
    SplineStopSpawner _stops;

    void Start() => Resolve();

    void Resolve()
    {
        if (_bus == null)
        {
            BusController bc = BusController.Instance != null ? BusController.Instance : FindAnyObjectByType<BusController>();
            if (bc != null) { _bc = bc; _bus = bc.transform; }
        }
        if (_tiled == null) _tiled = FindAnyObjectByType<TiledRoadStreamer>();
        if (_stops == null) _stops = FindAnyObjectByType<SplineStopSpawner>();
    }

    void LateUpdate()
    {
        if (!recenter) return;
        if (_bus == null) { Resolve(); if (_bus == null) return; }

        Vector3 flat = _bus.position;
        flat.y = 0f;                                    // keep ground level; only recenter on the plane
        if (flat.magnitude < threshold) return;

        Shift(-flat);
    }

    void Shift(Vector3 delta)
    {
        // Move EVERY root object exactly once — road, stops, pool, the bus root, AND the detached physics
        // sphere (which is its own scene root at runtime). One uniform pass = nothing double-shifts and
        // nothing desyncs. (Earlier bug: shifting the bus "separately" on top of the generic pass moved
        // it twice → it overshot to -2×threshold, or its sphere lagged → it flew into the sky.)
        for (int z = 0; z < SceneManager.sceneCount; z++)
            foreach (GameObject g in SceneManager.GetSceneAt(z).GetRootGameObjects())
                g.transform.position += delta;

        // The sphere is a Rigidbody: keep its physics-side position in lockstep with its transform and
        // flush interpolation so the interpolated bus doesn't smear from the old far pose for one frame
        // (that smear is what looked like a sky-launch / clipping).
        if (_bc != null && _bc.sphere != null)
        {
            _bc.sphere.position = _bc.sphere.transform.position;   // transform already shifted above
            RigidbodyInterpolation mode = _bc.sphere.interpolation;
            _bc.sphere.interpolation = RigidbodyInterpolation.None;
            _bc.sphere.interpolation = mode;
        }

        // Notify systems that cache WORLD positions, so they don't snap back to the pre-shift spot.
        if (_tiled != null) _tiled.OnOriginShifted(delta);
        if (_stops != null) _stops.OnOriginShifted(delta);

        // Push all the moved transforms into the physics engine in ONE controlled call. Otherwise the
        // next FixedUpdate's raycasts/queries trigger an implicit, poorly-timed sync (broadphase
        // re-insert of the big road collider) that can stall mid-step — the "game paused" spike.
        Physics.SyncTransforms();
    }
}
