using UnityEngine;

namespace Pieced.Environment
{
    /// <summary>
    /// Generates a section of a cylinder wall facing local -Z, used to wrap a decal around the heavy bag.
    /// UV (0,0) is bottom-left as seen from the front.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class CurvedPatch : MonoBehaviour
    {
        [SerializeField] private float radius = 0.314f;
        [SerializeField] private float halfArcDegrees = 48f;
        [SerializeField] private float height = 0.52f;
        [SerializeField, Range(2, 64)] private int segments = 24;

        private Mesh mesh;
        private bool dirty;

        private void OnEnable() => Rebuild();
        private void OnValidate() => dirty = true;

        private void Update()
        {
            if (!dirty) return;
            dirty = false;
            Rebuild();
        }

        private void OnDestroy()
        {
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
        }

        private void Rebuild()
        {
            if (mesh == null) mesh = new Mesh { name = "Curved Patch", hideFlags = HideFlags.DontSave };
            mesh.Clear();

            var columns = segments + 1;
            var vertices = new Vector3[columns * 2];
            var normals = new Vector3[columns * 2];
            var uvs = new Vector2[columns * 2];
            for (int i = 0; i < columns; i++)
            {
                float u = i / (float)segments;
                float theta = Mathf.Lerp(-halfArcDegrees, halfArcDegrees, u) * Mathf.Deg2Rad;
                var normal = new Vector3(Mathf.Sin(theta), 0f, -Mathf.Cos(theta));
                for (int row = 0; row < 2; row++)
                {
                    int index = i * 2 + row;
                    vertices[index] = normal * radius + Vector3.up * ((row - 0.5f) * height);
                    normals[index] = normal;
                    uvs[index] = new Vector2(u, row);
                }
            }

            var triangles = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                int bottomLeft = i * 2, topLeft = i * 2 + 1, bottomRight = i * 2 + 2, topRight = i * 2 + 3;
                triangles[i * 6 + 0] = bottomLeft; triangles[i * 6 + 1] = topLeft; triangles[i * 6 + 2] = bottomRight;
                triangles[i * 6 + 3] = topLeft; triangles[i * 6 + 4] = topRight; triangles[i * 6 + 5] = bottomRight;
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            GetComponent<MeshFilter>().sharedMesh = mesh;
        }
    }
}
