using System.Collections.Generic;
using System.Linq;
using UnityEngine;

struct Sphere
{
  public Vector3 position;
  public float radius;
  public Vector3 albedo;
  public Vector3 specular;
  public float smoothness;
  public Vector3 emission;
}

struct MeshObject
{
  public Matrix4x4 localToWorldMatrix;
  public int indices_offset;
  public int indices_count;
}

public class RayTracer : MonoBehaviour
{
  private static bool meshObjectsNeedRebuilding = false;
  private static List<RayTracedObject> rayTracedObjects = new List<RayTracedObject>();

  private static List<MeshObject> meshObjects = new List<MeshObject>();
  private static List<Vector3> vertices = new List<Vector3>();
  private static List<int> indices = new List<int>();
  private ComputeBuffer meshObjectBuffer;
  private ComputeBuffer vertexBuffer;
  private ComputeBuffer indexBuffer;

  private Camera _camera;
  private RenderTexture renderTexture;
  private RenderTexture converged;

  private uint currentSample = 0;
  private Material addMaterial;

  public Texture skyboxTexture;
  public ComputeShader rayTracerShader;
  public Light directionalLight;

  public int sphereSeed = 1223832719;
  public Vector2 sphereRadius = new Vector2(5.0f, 30.0f);
  public uint spheresMax = 10000;
  public float spherePlacementRadius = 1000.0f;
  private ComputeBuffer sphereBuffer;

  public static void RegisterObject(RayTracedObject obj)
  {
    rayTracedObjects.Add(obj);
    meshObjectsNeedRebuilding = true;
  }

  public static void UnregisterObject(RayTracedObject obj)
  {
    rayTracedObjects.Remove(obj);
    meshObjectsNeedRebuilding = true;
  }

  private void RebuildMeshObjectBuffers()
  {
    if (!meshObjectsNeedRebuilding)
    {
      return;
    }

    meshObjectsNeedRebuilding = false;
    currentSample = 0;

    meshObjects.Clear();
    vertices.Clear();
    indices.Clear();

    foreach (RayTracedObject obj in rayTracedObjects)
    {
      Mesh mesh = obj.GetComponent<MeshFilter>().sharedMesh;

      // Add vertex data
      int firstVertex = vertices.Count;
      vertices.AddRange(mesh.vertices);

      // Add index data if the vertex buffer wasn't empty before, the indices need to be offset
      int firstIndex = indices.Count;
      var meshIndices = mesh.GetIndices(0);
      indices.AddRange(meshIndices.Select(index => firstVertex + index));

      meshObjects.Add(new MeshObject()
      {
        localToWorldMatrix = obj.transform.localToWorldMatrix,
        indices_offset = firstIndex,
        indices_count = meshIndices.Length
      });
    }

    CreateComputeBuffer(ref meshObjectBuffer, meshObjects, 72);
    CreateComputeBuffer(ref vertexBuffer, vertices, 12);
    CreateComputeBuffer(ref indexBuffer, indices, 4);
  }

  private static void CreateComputeBuffer<T>(ref ComputeBuffer buffer, List<T> data, int stride)
    where T : struct
  {
    // Do we already have a compute buffer?
    if (buffer != null)
    {
      // If no data or buffer doesn't match the given criteria, release it
      if (data.Count == 0 || buffer.count != data.Count || buffer.stride != stride)
      {
        buffer.Release();
        buffer = null;
      }
    }

    if (data.Count != 0)
    {
      // If the buffer has been released or wasn't there to begin with, create it
      if (buffer == null)
      {
        buffer = new ComputeBuffer(data.Count, stride);
      }

      // Set the data on the buffer
      buffer.SetData(data);
    }
  }

  private void SetComputeBuffer(string name, ComputeBuffer buffer)
  {
    if (buffer != null)
    {
      rayTracerShader.SetBuffer(0, name, buffer);
    }
  }

  private void OnEnable()
  {
    currentSample = 0;
    SetUpScene();
  }

  private void OnDisable()
  {
    if (sphereBuffer != null)
    {
      sphereBuffer.Release();
    }
    if (indexBuffer != null)
    {
      indexBuffer.Release();
    }
    if (vertexBuffer != null)
    {
      vertexBuffer.Release();
    }
    if (meshObjectBuffer != null)
    {
      meshObjectBuffer.Release();
    }
  }

  private void SetUpScene()
  {
    Random.InitState(sphereSeed);

    List<Sphere> spheres = new List<Sphere>();

    // Add a number of random spheres
    for (int i = 0; i < spheresMax; i++)
    {
      Sphere sphere = new Sphere();

      // Position and radius
      sphere.radius = sphereRadius.x + Random.value * (sphereRadius.y - sphereRadius.x);
      Vector2 randomPos = Random.insideUnitCircle * spherePlacementRadius;
      sphere.position = new Vector3(randomPos.x, sphere.radius, randomPos.y);

      foreach (Sphere other in spheres)
      {
        float minDist = sphere.radius + other.radius;
        if (Vector3.SqrMagnitude(sphere.position - other.position) < minDist * minDist)
        {
          goto SkipSphere;
        }
      }

      // Albedo and specular
      Color color = Random.ColorHSV();
      float chance = Random.value;
      if (chance < 0.8f)
      {
        bool metal = chance < 0.4f;
        sphere.albedo = metal ? Vector3.zero : new Vector3(color.r, color.g, color.b);
        sphere.specular = metal ? new Vector3(color.r, color.g, color.b) : new Vector3(0.04f, 0.04f, 0.04f);
        sphere.smoothness = Random.value;
      }
      else
      {
        Color emission = Random.ColorHSV(0.0f, 1.0f, 0.0f, 1.0f, 3.0f, 8.0f);
        sphere.emission = new Vector3(emission.r, emission.g, emission.b);
      }

      spheres.Add(sphere);

    SkipSphere:
      continue;
    }

    // Assign to compute buffer
    sphereBuffer = new ComputeBuffer(spheres.Count, 56);
    sphereBuffer.SetData(spheres);
  }

  private void Awake()
  {
    _camera = GetComponent<Camera>();
  }

  void Update()
  {
    if (transform.hasChanged || directionalLight.transform.hasChanged)
    {
      currentSample = 0;
      transform.hasChanged = false;
      directionalLight.transform.hasChanged = false;
    }
  }

  private void SetShaderParameters()
  {
    rayTracerShader.SetMatrix("cameraToWorld", _camera.cameraToWorldMatrix);
    rayTracerShader.SetMatrix("cameraInverseProjection", _camera.projectionMatrix.inverse);
    rayTracerShader.SetTexture(0, "skyboxTexture", skyboxTexture);
    rayTracerShader.SetVector("pixelOffset", new Vector2(Random.value, Random.value));
    Vector3 l = directionalLight.transform.forward;
    rayTracerShader.SetVector("directionalLight", new Vector4(l.x, l.y, l.z, directionalLight.intensity));
    rayTracerShader.SetFloat("seed", Random.value);
    SetComputeBuffer("spheres", sphereBuffer);
    SetComputeBuffer("meshObjects", meshObjectBuffer);
    SetComputeBuffer("vertices", vertexBuffer);
    SetComputeBuffer("indices", indexBuffer);
  }

  private void OnRenderImage(RenderTexture source, RenderTexture destination)
  {
    RebuildMeshObjectBuffers();
    SetShaderParameters();
    Render(destination);
  }

  private void Render(RenderTexture destination)
  {
    InitRenderTexture();

    rayTracerShader.SetTexture(0, "result", renderTexture);
    int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
    int threadGroupsY = Mathf.CeilToInt(Screen.height / 8.0f);
    rayTracerShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);

    if (addMaterial == null)
    {
      addMaterial = new Material(Shader.Find("Hidden/AddShader"));
    }
    addMaterial.SetFloat("_Sample", currentSample);

    Graphics.Blit(renderTexture, converged, addMaterial);
    Graphics.Blit(converged, destination);

    currentSample++;
  }

  private void InitRenderTexture()
  {
    if (renderTexture == null || renderTexture.width != Screen.width || renderTexture.height != Screen.height)
    {
      if (renderTexture != null)
      {
        renderTexture.Release();
      }
      currentSample = 0;
      renderTexture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
      renderTexture.enableRandomWrite = true;
      renderTexture.Create();
    }
    if (converged == null || converged.width != Screen.width || converged.height != Screen.height)
    {
      if (converged != null)
      {
        converged.Release();
      }
      currentSample = 0;
      converged = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
      converged.enableRandomWrite = true;
      converged.Create();
    }
  }
}
