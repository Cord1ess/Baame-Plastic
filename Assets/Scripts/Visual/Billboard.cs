using UnityEngine;

/// Rotates this object to face the camera each frame — the 2.5D billboard trick. Default keeps the
/// sprite UPRIGHT (yaw only), like a standing person; FaceCamera fully aligns it to the camera plane.
/// Put this on any sprite that should always face the player (passengers, conductors, crowd, props).
public class Billboard : MonoBehaviour
{
    public enum Mode { UprightYAxis, FaceCamera }
    public Mode mode = Mode.UprightYAxis;

    Camera _cam;

    void LateUpdate()
    {
        if (_cam == null)
        {
            _cam = Camera.main;
            if (_cam == null) _cam = FindAnyObjectByType<Camera>();
            if (_cam == null) return;
        }

        if (mode == Mode.FaceCamera)
        {
            transform.forward = _cam.transform.forward;
        }
        else
        {
            Vector3 f = _cam.transform.forward;
            f.y = 0f;                                  // stay vertical (no pitch/roll)
            if (f.sqrMagnitude > 0.0001f) transform.forward = f.normalized;
        }
    }
}
