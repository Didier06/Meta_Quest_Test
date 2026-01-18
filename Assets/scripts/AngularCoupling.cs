using UnityEngine;

public class AngularCoupling : MonoBehaviour
{
    public HingeJoint pendulum1;
    public HingeJoint pendulum2;

    public float C = 500f; // J'ai baissé la valeur par défaut pour éviter l'explosion

    float timer = 0f;

    void FixedUpdate()
    {
        if (pendulum1 == null || pendulum2 == null) return;

        Rigidbody rb1 = pendulum1.GetComponent<Rigidbody>();
        Rigidbody rb2 = pendulum2.GetComponent<Rigidbody>();

        // Safety: Do not apply forces if physics is paused/kinematic (e.g. during reset)
        if (rb1.isKinematic || rb2.isKinematic) return;

        float theta1 = pendulum1.angle * Mathf.Deg2Rad;
        float theta2 = pendulum2.angle * Mathf.Deg2Rad;

        float torque = -C * (theta1 - theta2);

        // Safety: Check for NaN or Infinity to prevent crash
        if (float.IsNaN(torque) || float.IsInfinity(torque))
        {
            // Debug.LogWarning("AngularCoupling: NaN/Infinity torque detected. Skipping frame.");
            return;
        }

        // Safety: Clamp torque to prevent explosion
        torque = Mathf.Clamp(torque, -10000f, 10000f);

        Vector3 axis1 = pendulum1.transform.TransformDirection(pendulum1.axis).normalized;
        Vector3 axis2 = pendulum2.transform.TransformDirection(pendulum2.axis).normalized;

        rb1.AddTorque(axis1 * torque, ForceMode.Acceleration);
        rb2.AddTorque(-axis2 * torque, ForceMode.Acceleration);

        // --- DEBUG toutes les 0.5 s ---
        timer += Time.fixedDeltaTime;
        if (timer > 0.5f)
        {
            // Debug.Log($"θ1={theta1:F2} rad | θ2={theta2:F2} rad | Δ={theta1 - theta2:F2} | τ={torque:F2}");
            timer = 0f;
        }
    }
}

