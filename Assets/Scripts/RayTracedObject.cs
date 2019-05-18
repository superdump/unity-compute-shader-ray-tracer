using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshFilter))]
public class RayTracedObject : MonoBehaviour
{
  private void OnEnable()
  {
    RayTracer.RegisterObject(this);
  }

  private void OnDisable()
  {
    RayTracer.UnregisterObject(this);
  }
}
