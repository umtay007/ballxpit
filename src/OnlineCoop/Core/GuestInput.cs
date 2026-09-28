namespace BALLxPITOnlineCoop.Core;

public enum GuestAimMode
{
    /// <summary>The guest has not aimed yet.</summary>
    None = 0,
    /// <summary>Aim at a point on the streamed picture (0..1, origin top-left).</summary>
    Pointer = 1,
    /// <summary>Aim along a direction (controller stick), y up.</summary>
    Direction = 2,
}

/// <summary>The latest input the guest playing P2 sent.</summary>
public struct GuestInput
{
    public float MoveX;
    public float MoveY;
    public GuestAimMode AimMode;
    public float AimX;
    public float AimY;
    public bool Shoot;
    /// <summary>Seconds on the server's clock when this arrived.</summary>
    public double ReceivedAt;
}
