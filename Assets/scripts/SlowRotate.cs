using UnityEngine;

public class SlowRotate : MonoBehaviour
{
    public float rotationSpeed = 10f;   // degrés par seconde

    void Update()
    {
        transform.Rotate(0f, rotationSpeed * Time.deltaTime, 0f);
    }

    // Fonction facile à trouver dans le menu Unity
    public void SetRotating(bool isRotating)
    {
        this.enabled = isRotating;
    }
}

