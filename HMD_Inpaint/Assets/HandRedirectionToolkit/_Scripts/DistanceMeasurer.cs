using UnityEngine;
using TMPro;

public class DistanceMeasurer : MonoBehaviour
{
    [SerializeField]
    private Transform rightControllerTransform;

    public Transform targetObject;

    public float distance;

    void Start()
    {
        if (rightControllerTransform == null)
        {
            Debug.LogError("RightControllerTransform is not set. Please assign it in the Inspector.");
        }
    }

    void Update()
    {
        if (rightControllerTransform != null && targetObject != null)
        {
            distance = Vector3.Distance(rightControllerTransform.position, targetObject.position) * 100;
        }
    }
}
