using UnityEngine;
using UnityEngine.InputSystem;

/// Central input for the game on the NEW Input System, defined in code so there's no .inputactions
/// asset to author/wire — keyboard AND gamepad both work immediately. Two action sets:
///   • Driving  — accelerate / brake / steer / drift   (the bus)
///   • OnFoot   — move / action / altAction            (the conductors)
/// RoleController enables exactly ONE set at a time, so input can never leak to the bus while you're
/// controlling a conductor (that's the "bus moves while conducting" fix). ToggleRole is always on.
///
/// Auto-creates itself on first access — you don't place it in the scene. To rebind later, this is the
/// one file to edit (or we can swap to an .inputactions asset without touching the gameplay scripts).
public class GameInput : MonoBehaviour
{
    static GameInput _instance;
    public static GameInput Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindAnyObjectByType<GameInput>();
                if (_instance == null) _instance = new GameObject("GameInput").AddComponent<GameInput>();
            }
            return _instance;
        }
    }

    // Driving
    public InputAction accelerate;
    public InputAction brake;
    public InputAction steer;   // -1..1
    public InputAction drift;

    // On foot (conductors)
    public InputAction move;    // Vector2
    public InputAction action;  // grab / throw / haggle
    public InputAction altAction; // throw

    // Global
    public InputAction toggleRole;
    public InputAction horn;

    bool _built;

    void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;
        Build();
        EnableDriving();
        toggleRole.Enable();
        horn.Enable();
    }

    void Build()
    {
        if (_built) return;
        _built = true;

        accelerate = new InputAction("Accelerate", InputActionType.Button);
        accelerate.AddBinding("<Keyboard>/w");
        accelerate.AddBinding("<Keyboard>/upArrow");
        accelerate.AddBinding("<Gamepad>/rightTrigger");
        accelerate.AddBinding("<Gamepad>/buttonSouth");

        brake = new InputAction("Brake", InputActionType.Button);
        brake.AddBinding("<Keyboard>/s");
        brake.AddBinding("<Keyboard>/downArrow");
        brake.AddBinding("<Gamepad>/leftTrigger");
        brake.AddBinding("<Gamepad>/buttonEast");

        steer = new InputAction("Steer", InputActionType.Value, expectedControlType: "Axis");
        steer.AddCompositeBinding("1DAxis")
            .With("Negative", "<Keyboard>/a")
            .With("Positive", "<Keyboard>/d");
        steer.AddCompositeBinding("1DAxis")
            .With("Negative", "<Keyboard>/leftArrow")
            .With("Positive", "<Keyboard>/rightArrow");
        steer.AddBinding("<Gamepad>/leftStick/x");

        drift = new InputAction("Drift", InputActionType.Button);
        drift.AddBinding("<Keyboard>/space");
        drift.AddBinding("<Gamepad>/rightShoulder");

        move = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
        move.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        move.AddBinding("<Gamepad>/leftStick");

        action = new InputAction("Action", InputActionType.Button);
        action.AddBinding("<Keyboard>/e");
        action.AddBinding("<Gamepad>/buttonSouth");

        altAction = new InputAction("AltAction", InputActionType.Button);
        altAction.AddBinding("<Keyboard>/q");
        altAction.AddBinding("<Gamepad>/buttonWest");

        toggleRole = new InputAction("ToggleRole", InputActionType.Button);
        toggleRole.AddBinding("<Keyboard>/c");
        toggleRole.AddBinding("<Gamepad>/start");

        horn = new InputAction("Horn", InputActionType.Button);   // always on — any role can honk
        horn.AddBinding("<Keyboard>/h");
        horn.AddBinding("<Gamepad>/buttonNorth");
    }

    public void EnableDriving()
    {
        accelerate.Enable(); brake.Enable(); steer.Enable(); drift.Enable();
        move.Disable(); action.Disable(); altAction.Disable();
    }

    public void EnableOnFoot()
    {
        accelerate.Disable(); brake.Disable(); steer.Disable(); drift.Disable();
        move.Enable(); action.Enable(); altAction.Enable();
    }

    void OnDestroy()
    {
        if (_instance != this) return;
        accelerate?.Dispose(); brake?.Dispose(); steer?.Dispose(); drift?.Dispose();
        move?.Dispose(); action?.Dispose(); altAction?.Dispose(); toggleRole?.Dispose(); horn?.Dispose();
        _instance = null;
    }
}
