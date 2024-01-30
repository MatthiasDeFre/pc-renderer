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
using System.Collections.Generic;
using UnityEngine.UI;
using System.IO;

public class PCReceiver : MonoBehaviour
{
    [DllImport("PCStreamingPlugin")]
    static extern int setup_connection(string addr, UInt32 port);
    [DllImport("PCStreamingPlugin")]
    private static extern int start_listening();
    [DllImport("PCStreamingPlugin")]
    private static extern int next_frame();
    [DllImport("PCStreamingPlugin")]
    // You should technically be able to pass any type of pointer (array) to the plugin, however this has not yet been tested
    // This means that you should be able to pass an array of structures, i.e. points, and that the array should fill itself
    // And that you don't need to do any parsing in Unity (however, not yet tested)
    private static extern int set_data(byte[] points);
    [DllImport("PCStreamingPlugin")]
    private static extern void clean_up();
    [DllImport("PCStreamingPlugin")]
    private static extern int send_data_to_server(byte[] data, uint size);
    private MeshFilter meshFilter;
    private int num;
    private int numTemp;
    private int currentFrame = 0;
    private bool isDecoding = false;
    private bool frameReady = false;
    private byte[] data;
    private Mesh.MeshDataArray meshDataArray;
    private List<Task<DracoMeshLoader.DecodeResult>> decodeTasks;
    private List<int> sizes;
    private int nLayers;
    private Mesh mesh;
    private long previousAnimationTime;
    private long decodeStartTime;
    private long frameReadyTime;
    private long frameIdleTime;

    private StreamWriter writer;

    public Text animLatency;
    public Text decodeLatency;
    public Text frameReadyLatency;
    public Text temp;

    public GameObject HQ;
    public GameObject MQ;
    public GameObject LQ;

    private MeshFilter hqFilter;
    private MeshFilter mqFilter;
    private MeshFilter lqFilter;

    private int quality = 0;
    // Start is called before the first frame update
    void Start()
    {
        // If this functions returns 0 everything is fine, 1=>WSA startup error, 2=>socket creation error, 3=>sendto (L4S client) error
        Debug.Log(setup_connection("127.0.0.1", 8000));
        start_listening();
        meshFilter =GetComponent<MeshFilter>();
        hqFilter= HQ.GetComponent<MeshFilter>();
        mqFilter = MQ.GetComponent<MeshFilter>();
        lqFilter = LQ.GetComponent<MeshFilter>();
        decodeTasks = new();
        sizes= new();
        previousAnimationTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        writer = new StreamWriter("output.csv", false);
        writer.WriteLine("frame_nr;dc_latency;interframe_latency;idle_time;quality;timestamp;size");
        writer.Flush();
    }

    // Update is called once per frame
    void Update()
    {
        if (decodeTasks.Count() > 0 && decodeTasks.All(t => t.IsCompleted))
        {
            float decodeEnd = Time.realtimeSinceStartup;

            //Debug.Log("decode complete" + decodeTask.Result + " " + data.Length.ToString());
            isDecoding = false;
            if (decodeTasks.All(t => t.Result.success))
            {
                //Debug.Log(meshDataArray[0].vertexCount);
                int totalSize = 0;
                for(int i = 0; i < nLayers; i++)
                {
                    totalSize+= meshDataArray[i].vertexCount;
                }
                // Apply onto new Mesh
                Destroy(mesh);
                mesh = new Mesh();
                var col = new NativeArray<Color32>(totalSize, Allocator.TempJob);
                var pos = new NativeArray<Vector3>(totalSize, Allocator.TempJob);
                int offset = 0;
                for (int i = 0; i < nLayers; i++)
                {
                    meshDataArray[i].GetColors(col.GetSubArray(offset, meshDataArray[i].vertexCount));
                    meshDataArray[i].GetVertices(pos.GetSubArray(offset, meshDataArray[i].vertexCount));
                    offset += meshDataArray[i].vertexCount;
                }
                //Debug.Log(meshDataArray[0].GetVertexData<float>().Length);
                //Debug.Log(meshDataArray[0].vertexCount);
                
                //Mesh.ApplyAndDisposeWritableMeshData(meshDataArray,mesh);
                mesh.indexFormat = totalSize > 65535 ?
                        IndexFormat.UInt32 : IndexFormat.UInt16;

                mesh.SetVertices(pos);
                mesh.SetColors(col);

                mesh.SetIndices(
                    Enumerable.Range(0, mesh.vertexCount).ToArray(),
                    MeshTopology.Points, 0
                );

                // Use the resulting mesh
                mesh.UploadMeshData(true);

                HQ.SetActive(false);
                MQ.SetActive(false);
                LQ.SetActive(false);
                if(quality >= 60)
                {
                    HQ.SetActive(true);
                    hqFilter.mesh = mesh;
                    Debug.Log("HQ");
                } else if(quality >= 40)
                {
                    MQ.SetActive(true);
                    mqFilter.mesh = mesh;
                    Debug.Log("MQ");
                } else
                {
                    LQ.SetActive(true);
                    lqFilter.mesh = mesh;
                    Debug.Log("LQ");
                }
                //meshFilter.mesh = mesh;
                col.Dispose();
                pos.Dispose();
                long currAnimationTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                long interframeLatency = currAnimationTime - previousAnimationTime;
                long decodeEndTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                animLatency.text = interframeLatency.ToString() + "ms";
                long decodeLatencyL = (decodeEndTime - decodeStartTime);
                decodeLatency.text = decodeLatencyL.ToString() + "ms";
                previousAnimationTime = currAnimationTime;
               // writer.WriteLine($"{(currentFrame-1)};{decodeLatencyL};{interframeLatency};{frameIdleTime};{quality};{decodeEndTime};{numTemp}");
                //writer.Flush();
            }
            meshDataArray.Dispose();
            decodeTasks.Clear();
            sizes.Clear();
        }
        if (!frameReady) {
            num = next_frame();
            int prevNum = num;
            frameReady = true;
            frameReadyTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }
        Debug.Log(num);
        temp.text = num.ToString();
        if (num > 50 && !isDecoding)
        {
            numTemp = num;
            quality = 0;
            decodeStartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            frameIdleTime = decodeStartTime - frameReadyTime;
            frameReadyLatency.text = frameIdleTime.ToString() + "ms";
            currentFrame++;
            frameReady = false;
            isDecoding = true;
            data = new byte[num];
            set_data(data);
            nLayers = (int)BitConverter.ToUInt32(data, 12);
            
            //nLayers = 1;
            //UInt32 layerId = BitConverter.ToUInt32(data, 28);
            meshDataArray = Mesh.AllocateWritableMeshData(nLayers);
            // Debug.Log("start decoding " + data.Length.ToString());
            int offset = 12 + 7 * 4;
            for (int i= 0; i < nLayers; i++)
            {
                UInt32 layerId = BitConverter.ToUInt32(data, offset);
                switch(layerId)
                {
                    case 0:
                        quality += 60;
                        break;
                    case 1:
                        quality += 25;
                        break;
                    case 2:
                        quality += 15;
                        break;
                }
                offset += 4;
                UInt32 layerSize = BitConverter.ToUInt32(data, offset);
                offset += 4;
                byte[] layerData = new byte[layerSize];
                Array.Copy(data, offset, layerData, 0, layerSize);
                offset += (int)layerSize;
                sizes.Add((int)layerSize);
                var draco = new DracoMeshLoader();
                decodeTasks.Add(draco.ConvertDracoMeshToUnity(
                    meshDataArray[i],
                    layerData,
                    false, // Set to true if you require normals. If Draco data does not contain them, they are allocated and we have to calculate them below
                    false// Retrieve tangents is not supported, but this will ensure they are allocated and can be calculated later (see below)
                ));
            }
            num = 0;
            // Async decoding has to start on the main thread and spawns multiple C# jobs.
            //decodeTask = 
        }
       /* int p_size = next_frame();
        if (p_size > 0)
        {
            byte[] p = new byte[p_size];
            Console.WriteLine(p_size);
            set_frame_data(p);
        }*/
    }
    void OnApplicationQuit()
    {
        writer.Close();
        clean_up();
    }
}
