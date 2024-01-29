using UnityEngine;
using System;
using UnityEngine.Rendering;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections;
using static UnityEngine.Mesh;
using Draco;
using System.Threading.Tasks;
using Unity.Collections;

public class L4SProxyTest : MonoBehaviour
{
    // You need to define these functions
    [DllImport("L4SProxyPlugin")]
    private static extern int setup_connection(string ip);
    [DllImport("L4SProxyPlugin")]
    private static extern int start_listening();
    [DllImport("L4SProxyPlugin")]
    private static extern int next_frame();
    [DllImport("L4SProxyPlugin")]
    // You should technically be able to pass any type of pointer (array) to the plugin, however this has not yet been tested
    // This means that you should be able to pass an array of structures, i.e. points, and that the array should fill itself
    // And that you don't need to do any parsing in Unity (however, not yet tested)
    private static extern int set_data(byte[] points);
    [DllImport("L4SProxyPlugin")]
    private static extern void clean_up();
    private MeshFilter meshFilter;
    private int num;
    private int currentFrame = 0;
    private bool isDecoding;
    private bool frameReady;
    private byte[] data;
    private Mesh.MeshDataArray meshDataArray;
    private Task<DracoMeshLoader.DecodeResult> decodeTask;
    // Start is called before the first frame update
    void Start()
    {
        // You'll want to change this ip to the ip of your WSL2 instance
        // If this functions returns 0 everything is fine, 1=>WSA startup error, 2=>socket creation error, 3=>sendto (L4S client) error
        Debug.Log(setup_connection("172.22.107.250"));
        // Calling this function will start a thread to receive data from the L4S client
        start_listening();
        meshFilter=GetComponent<MeshFilter>();
    }

    // Update is called once per frame
    void Update()
    {
        if (decodeTask != null && decodeTask.IsCompleted)
        {
            float decodeEnd = Time.realtimeSinceStartup;

            Debug.Log("decode complete" + decodeTask.Result + " " + data.Length.ToString());
            isDecoding = false;
            if (decodeTask.Result.success)
            {
                Debug.Log(meshDataArray[0].vertexCount);
                // Apply onto new Mesh
                var mesh = new Mesh();
                Debug.Log(meshDataArray[0].GetVertexData<float>().Length);
                Debug.Log(meshDataArray[0].vertexCount);
                var col = new NativeArray<Color32>(meshDataArray[0].vertexCount, Allocator.TempJob);
                var pos = new NativeArray<Vector3>(meshDataArray[0].vertexCount, Allocator.TempJob);
                meshDataArray[0].GetColors(col);
                meshDataArray[0].GetVertices(pos);
                //Mesh.ApplyAndDisposeWritableMeshData(meshDataArray,mesh);
                mesh.indexFormat = meshDataArray[0].vertexCount > 65535 ?
                        IndexFormat.UInt32 : IndexFormat.UInt16;

                mesh.SetVertices(pos);
                mesh.SetColors(col);

                mesh.SetIndices(
                    Enumerable.Range(0, mesh.vertexCount).ToArray(),
                    MeshTopology.Points, 0
                );

                // Use the resulting mesh
                mesh.UploadMeshData(true);
                meshFilter.mesh = mesh;
                col.Dispose();
                pos.Dispose();
            }
            meshDataArray.Dispose();
            decodeTask = null;
        }
        if (!frameReady) {
            num = next_frame();
        }
        
        if (num > 0 && !isDecoding)
        {
        currentFrame++;
        frameReady = false;
        isDecoding = true;
        data = new byte[num];
        set_data(data);
        Debug.Log("start decoding " + data.Length.ToString());

        var draco = new DracoMeshLoader();
        meshDataArray = Mesh.AllocateWritableMeshData(1);
        // Async decoding has to start on the main thread and spawns multiple C# jobs.
        decodeTask = draco.ConvertDracoMeshToUnity(
            meshDataArray[0],
            data,
            false, // Set to true if you require normals. If Draco data does not contain them, they are allocated and we have to calculate them below
            false// Retrieve tangents is not supported, but this will ensure they are allocated and can be calculated later (see below)
            );
        }
    }
    void OnApplicationQuit()
    {
        clean_up();
    }
}
