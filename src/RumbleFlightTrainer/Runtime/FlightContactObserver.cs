using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace RumbleFlightTrainer;

public sealed class FlightContactObserver : MonoBehaviour
{
    private StructureTelemetry? telemetry;
    private int receiverBodyId;

    public FlightContactObserver(IntPtr pointer) : base(pointer) { }

    [HideFromIl2Cpp]
    internal void Bind(StructureTelemetry? capture, int bodyId)
    {
        telemetry = capture;
        receiverBodyId = bodyId;
    }

    private void OnCollisionEnter(Collision collision) => telemetry?.ReportContact(receiverBodyId, collision, "enter");
    private void OnCollisionStay(Collision collision) => telemetry?.ReportContact(receiverBodyId, collision, "stay");
    private void OnCollisionExit(Collision collision) => telemetry?.ReportContact(receiverBodyId, collision, "exit");
}
