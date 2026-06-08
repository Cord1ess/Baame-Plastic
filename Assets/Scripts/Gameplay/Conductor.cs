using UnityEngine;

/// Conductor 1 — the door conductor you control (toggle via RoleController). A billboard you move with
/// WASD (camera-relative). Press Grab to scoop the nearest waiting passenger and carry them; press
/// again (or Throw) to send them at the bus door to board — handy for recruiting the ones who weren't
/// going to board. When you're not controlling him he rides at the bus door (his "home"). No physics.
public class Conductor : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float grabRange = 3f;

    BillboardCharacter _view;
    Transform _home;
    Camera _cam;
    bool _controlled;
    Passenger _held;

    public void Setup(BillboardCharacter view, Transform home)
    {
        _view = view;
        _home = home;
        _cam = Camera.main;
        ReturnHome();
    }

    public void SetControlled(bool on)
    {
        _controlled = on;
        if (_cam == null) _cam = Camera.main;
        if (on)
        {
            transform.SetParent(null, true);          // detach from the bus to run around
            if (_view != null) _view.ApplyHeight();
        }
        else ReturnHome();
    }

    void ReturnHome()
    {
        if (_held != null) { _held.BeginBoarding(BusPassengers.Instance); _held = null; }
        if (_home != null)
        {
            transform.SetParent(_home, false);
            transform.localPosition = Vector3.zero;
            if (_view != null) _view.ApplyHeight();
        }
    }

    void Update()
    {
        if (!_controlled) return;
        if (_cam == null) { _cam = Camera.main; if (_cam == null) return; }

        GameInput gi = GameInput.Instance;
        Vector2 mv = gi.move.ReadValue<Vector2>();
        Vector3 fwd = _cam.transform.forward; fwd.y = 0f; fwd.Normalize();
        Vector3 right = _cam.transform.right; right.y = 0f; right.Normalize();
        Vector3 moveDir = fwd * mv.y + right * mv.x;
        if (moveDir.sqrMagnitude > 0.01f)
            transform.position += moveDir.normalized * moveSpeed * Time.deltaTime;

        if (_held != null)
            _held.transform.position = transform.position + fwd * 0.6f;   // carry in front

        if (gi.action.WasPressedThisFrame()) { if (_held == null) TryGrab(); else ThrowHeld(); }
        if (gi.altAction.WasPressedThisFrame()) ThrowHeld();
    }

    void TryGrab()
    {
        Passenger best = null;
        float bestSqr = grabRange * grabRange;
        foreach (Passenger p in FindObjectsByType<Passenger>(FindObjectsInactive.Exclude))
        {
            if (p.state != Passenger.State.Waiting && p.state != Passenger.State.Gathering) continue;
            float d = (p.transform.position - transform.position).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = p; }
        }
        if (best != null) { best.Grab(); _held = best; }
    }

    void ThrowHeld()
    {
        if (_held == null) return;
        _held.ThrowTo(BusPassengers.Instance);   // arc to the door, then board on landing
        _held = null;
    }
}
