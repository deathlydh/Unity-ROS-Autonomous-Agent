using UnityEngine;

[ExecuteInEditMode]
public class ObjectDimensionViewer : MonoBehaviour
{
    void Update()
    {
        // Try getting MeshFilter size
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            Vector3 size = Vector3.Scale(meshFilter.sharedMesh.bounds.size, transform.lossyScale);
            Debug.Log($"[Dimensions] {gameObject.name}: Width={size.x:F2}, Height={size.y:F2}, Depth={size.z:F2} (Unity Units)");
        }
        else
        {
            // If it's a group or has no mesh filter, try Renderer bounds
            Renderer renderer = GetComponent<Renderer>();
            if (renderer != null)
            {
                Vector3 size = renderer.bounds.size;
                Debug.Log($"[Dimensions] {gameObject.name}: Width={size.x:F2}, Height={size.y:F2}, Depth={size.z:F2} (Unity Units)");
            }
        }
    }
}
