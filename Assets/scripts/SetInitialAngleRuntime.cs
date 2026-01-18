using UnityEngine;

public class SetInitialAngleRuntime : MonoBehaviour
{
    public HingeJoint hinge;
    public float degrees = 10f;

    void Start()
    {
        Vector3 axis = hinge.transform.TransformDirection(hinge.axis);
        Vector3 pivot = hinge.transform.TransformPoint(hinge.anchor);

        transform.RotateAround(pivot, axis, degrees);
    }
}

