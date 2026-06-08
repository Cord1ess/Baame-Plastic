using System.Collections.Generic;
using UnityEngine;

/// Global pool of placeholder passengers, pre-built at load so nothing is Instantiated mid-game (keeps
/// streaming smooth). Bus stops borrow waiting passengers via Take(); they're Returned when a stop gives
/// up un-boarded ones, or when an aboard passenger leaves the bus. SplineStopSpawner auto-creates one if
/// none exists; you don't place it by hand.
public class PassengerPool : MonoBehaviour
{
    public static PassengerPool Instance { get; private set; }

    public int poolSize = 250;
    public float passengerHeight = 1.8f;

    readonly Queue<Passenger> _free = new Queue<Passenger>();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        for (int i = 0; i < poolSize; i++) _free.Enqueue(CreateOne(i));
    }

    Passenger CreateOne(int i)
    {
        BillboardCharacter v = BillboardCharacter.Create("Passenger_" + i, Color.grey, passengerHeight, Vector3.zero, transform);
        Passenger p = v.gameObject.AddComponent<Passenger>();
        p.Setup(v);
        p.Hide();
        return p;
    }

    public Passenger Take()
    {
        return _free.Count > 0 ? _free.Dequeue() : null;
    }

    public void Return(Passenger p)
    {
        if (p == null) return;
        p.Hide();
        p.transform.SetParent(transform, false);
        _free.Enqueue(p);
    }
}
