// Copyright 2017 The Draco Authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
#define SHOW_RESULTS
//#define SAVE_RESULTS

using UnityEngine.Rendering;
using System.IO;
using UnityEngine;
using Draco;
using Unity.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine.UI;
using WebSocketSharp;
using Unity.WebRTC;
using System.Collections;
using System.Runtime.CompilerServices;
using UnityEngine.XR;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class DracoDemoMeshData : MonoBehaviour
{

    public string filePath;

    public bool requireNormals;
    public bool requireTangents;
    public Text decodeTime;
    public Text animationFPS;
    public Text pcLatency;
    public Text timeBetweenPackets;
    private Task<DracoMeshLoader.DecodeResult> decodeTask;
    private bool isDecoding;
    private byte[] data;
    private Mesh.MeshDataArray meshDataArray;
    private MeshFilter filter;
    private float start;
    private WebSocket ws;
    private string localDescription;
    private string localCandidate;
    private int phase = 0;
    private int msg_idx = 0;
    private int pcSize = int.MaxValue;
    private List<byte> enc_data = new List<byte>();
    private const int FRAME_BUFFER_SIZE = 2;
    private List<byte[]> frames = new List<byte[]>(2);
    private int currentDecodeFrameIdx = 0;
    private int currentRenderFrameIdx = 0;
    private const int FRAGMENT_SIZE = 16000;
    private bool frameReady = false;
    private float timeSinceLastFrame;
    private float timeSinceFirstPacket;
    private float timeSinceLastPacketReceived;
    private int currentFrame = 0;
    #if SAVE_RESULTS
    private const int MAX_FRAMES = 1000;
    private bool resultsWritten = false;
    private List<float> decodeTimeResults = new List<float>(MAX_FRAMES);
    private List<float> animationFPSResults = new List<float>(MAX_FRAMES);
    private List<float> pcLatencyResults = new List<float>(MAX_FRAMES);
    private List<float> timeBetweenPacketsResults = new List<float>(MAX_FRAMES);
    #endif
    public void Start()
    {
        filter = GetComponent<MeshFilter>();
        if (BitConverter.IsLittleEndian) { Debug.Log("little"); } else { Debug.Log("big"); };
        //data = File.ReadAllBytes(filePath);
        //if (data == null) return;
        timeSinceLastFrame = Time.realtimeSinceStartup;
        timeSinceLastPacketReceived = Time.realtimeSinceStartup;
        ws = new WebSocket("ws://localhost:8000");

        ws.OnMessage += (sender, e) =>
        {
            Debug.Log("Message Received from " + ((WebSocket)sender).Url + ", Data : " + e.Data);
            if (e.Data[0] == 'd')
            {
                Debug.Log("received desc");
                remoteDescription = e.Data[1..];
                Debug.Log("data=" + remoteDescription);
                phase = 1;

                Debug.Log("desc=" + remoteDescription);
            }
            else if (e.Data[0] == 'c')
            {
                phase = 3;

                remoteCandidate = e.Data[1..];
                Debug.Log("received candidate=" + remoteCandidate);
            }
        };
        ws.OnOpen += (sender, e) =>
        {
            Debug.Log("Ws opened");
            ws.Send("oHello");
        };

        ws.Connect();
        pc2OnIceConnectionChange = state => { OnIceConnectionChange(pc2, state); };
        pc2OnIceCandidate = candidate => { OnIceCandidate(pc2, candidate); };
        onDataChannel = channel =>
        {
            remoteDataChannel = channel;
            remoteDataChannel.OnMessage = onDataChannelMessage;
        };
        onDataChannelMessage = bytes => {
            Debug.Log(bytes.Length);
            Debug.Log("RANDOMWORD " + msg_idx);
            //Debug.Log("MESSAGE " + BitConverter.ToUInt32(bytes));
            msg_idx++;
            if (msg_idx == 1)
            {
                Debug.Log("SIZE OF MESSAGE " + BitConverter.ToUInt32(bytes));
                timeSinceFirstPacket = Time.realtimeSinceStartup;
                pcSize = BitConverter.ToInt32(bytes);
            }
            else
            {
                enc_data.AddRange(bytes);
                Debug.Log("MESSAGE " + bytes[0]);
                Debug.Log(enc_data.Count);
                if (enc_data.Count == pcSize)
                {
                    //File.WriteAllBytes("tt.drc", enc_data.ToArray());
                    Debug.Log("size " + currentDecodeFrameIdx + " " + frames.Capacity);
                    data = enc_data.ToArray();
                    enc_data.Clear();
                    currentDecodeFrameIdx = (currentDecodeFrameIdx + 1) % FRAME_BUFFER_SIZE;
                    pcSize = 0;
                    msg_idx = 0;
                    frameReady = true;
#if SHOW_RESULTS
                    float pcLatencyF = (Time.realtimeSinceStartup - timeSinceFirstPacket) * 1000;
                    float timeBetweenPacketsF = (Time.realtimeSinceStartup - timeSinceLastPacketReceived) * 1000;
                    pcLatency.text = (pcLatencyF).ToString();
                    timeBetweenPackets.text = (timeBetweenPacketsF).ToString();
                    timeSinceLastPacketReceived = Time.realtimeSinceStartup;
#if SAVE_RESULTS
                    pcLatencyResults.Add(pcLatencyF);
                    timeBetweenPacketsResults.Add(timeBetweenPacketsF);
#endif
#endif
                }
            }

        };
        onDataChannelOpen = () =>
        {
            Debug.Log("open");
        };
        onDataChannelClose = () =>
        {
            Debug.Log("close");
        };


    }
    public void Update()
    {
        if (phase == 1)
        {
            StartCoroutine(Call());
        }
        else if (phase == 3)
        {
            RTCIceCandidateInit can = new RTCIceCandidateInit();
            can.candidate = remoteCandidate;
            can.sdpMid = "0";
            pc2.AddIceCandidate(new RTCIceCandidate(can));
            ws.Send('c' + localCandidate);
            phase = 4;
        }
        if (decodeTask != null && decodeTask.IsCompleted)
        {
            float decodeEnd = Time.realtimeSinceStartup;
            
            Debug.Log("decode complete" + decodeTask.Result + " " + data.Length.ToString());
            isDecoding = false;
            if (decodeTask.Result.success)
            {
                currentRenderFrameIdx = (currentRenderFrameIdx + 1) % FRAME_BUFFER_SIZE;
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
                filter.mesh = mesh;
                col.Dispose();
                pos.Dispose();
            }
            meshDataArray.Dispose();
            decodeTask = null;
#if SHOW_RESULTS
            float end = Time.realtimeSinceStartup;
            float decodeTimeF = 1.0f / (decodeEnd - start);
            float animationFPSF = 1.0f / (end - timeSinceLastFrame);
            decodeTime.text = (decodeTimeF).ToString();
            animationFPS.text = (animationFPSF).ToString();
            timeSinceLastFrame = end;
#if SAVE_RESULTS
            decodeTimeResults.Add(decodeTimeF);
            animationFPSResults.Add(animationFPSF);
#endif
            
#endif
        }
#if SAVE_RESULTS
        if (!isDecoding && frameReady && currentFrame < MAX_FRAMES )
#else
        if (!isDecoding && frameReady)
#endif
        {
            currentFrame++;
            frameReady = false;
            isDecoding = true;
            Debug.Log("start decoding " + data.Length.ToString() + " " + enc_data.ToArray().Length.ToString() + " " + System.Text.Encoding.UTF8.GetString(data));

            start = Time.realtimeSinceStartup;
            var draco = new DracoMeshLoader();
            meshDataArray = Mesh.AllocateWritableMeshData(1);
            // Async decoding has to start on the main thread and spawns multiple C# jobs.
            decodeTask = draco.ConvertDracoMeshToUnity(
                meshDataArray[0],
                data,
                requireNormals, // Set to true if you require normals. If Draco data does not contain them, they are allocated and we have to calculate them below
                requireTangents // Retrieve tangents is not supported, but this will ensure they are allocated and can be calculated later (see below)
                );

        }
#if SAVE_RESULTS
        if(!resultsWritten && currentFrame == MAX_FRAMES && !isDecoding)
        {
            Debug.Log("Writing results");
            resultsWritten = true;
            using (StreamWriter writer = new StreamWriter("pipe_render.csv", false))
            {
                writer.WriteLine("frame_nr;dec_time;anim_fps;pc_latency;time_packets");
                for(int i=0; i<MAX_FRAMES; i++)
                {
                    writer.WriteLine(i + ";" + decodeTimeResults[i].ToString() + ";" + animationFPSResults[i].ToString() + ";" + pcLatencyResults[i].ToString() + ";" + timeBetweenPacketsResults[i].ToString());
                }
            }
        }
#endif
    }
    async void DDDD()
    {

        var data = File.ReadAllBytes(filePath);
        if (data == null) return;

        // Convert data to Unity mesh
        var draco = new DracoMeshLoader();

        // Allocate single mesh data (you can/should bulk allocate multiple at once, if you're loading multiple draco meshes) 
        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        float start = Time.realtimeSinceStartup;
        // Async decoding has to start on the main thread and spawns multiple C# jobs.
        var result = await draco.ConvertDracoMeshToUnity(
            meshDataArray[0],
            frames[currentRenderFrameIdx],
            requireNormals, // Set to true if you require normals. If Draco data does not contain them, they are allocated and we have to calculate them below
            requireTangents // Retrieve tangents is not supported, but this will ensure they are allocated and can be calculated later (see below)
            );
        float end = Time.realtimeSinceStartup;
        decodeTime.text = ((end - start) * 1000).ToString();
        Debug.Log((end - start) * 1000);
        if (result.success)
        {
            /* Debug.Log(meshDataArray[0].vertexCount);
             // Apply onto new Mesh
             var mesh = new Mesh();

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
             GetComponent<MeshFilter>().mesh = mesh;

             col.Dispose();
             pos.Dispose();*/
        }
        meshDataArray.Dispose();
    }

    private RTCPeerConnection pc1, pc2;
    private RTCDataChannel dataChannel, remoteDataChannel;
    private DelegateOnIceConnectionChange pc1OnIceConnectionChange;
    private DelegateOnIceConnectionChange pc2OnIceConnectionChange;
    private DelegateOnIceCandidate pc1OnIceCandidate;
    private DelegateOnIceCandidate pc2OnIceCandidate;
    private DelegateOnMessage onDataChannelMessage;
    private DelegateOnOpen onDataChannelOpen;
    private DelegateOnClose onDataChannelClose;
    private DelegateOnDataChannel onDataChannel;
    private string remoteDescription;
    private string remoteCandidate;

    RTCConfiguration GetSelectedSdpSemantics()
    {
        RTCConfiguration config = default;
        return config;
    }
    void OnIceConnectionChange(RTCPeerConnection pc, RTCIceConnectionState state)
    {
        switch (state)
        {
            case RTCIceConnectionState.New:
                Debug.Log($"{GetName(pc)} IceConnectionState: New");
                break;
            case RTCIceConnectionState.Checking:
                Debug.Log($"{GetName(pc)} IceConnectionState: Checking");
                break;
            case RTCIceConnectionState.Closed:
                Debug.Log($"{GetName(pc)} IceConnectionState: Closed");
                break;
            case RTCIceConnectionState.Completed:
                Debug.Log($"{GetName(pc)} IceConnectionState: Completed");
                break;
            case RTCIceConnectionState.Connected:
                Debug.Log($"{GetName(pc)} IceConnectionState: Connected");
                break;
            case RTCIceConnectionState.Disconnected:
                Debug.Log($"{GetName(pc)} IceConnectionState: Disconnected");
                break;
            case RTCIceConnectionState.Failed:
                Debug.Log($"{GetName(pc)} IceConnectionState: Failed");
                break;
            case RTCIceConnectionState.Max:
                Debug.Log($"{GetName(pc)} IceConnectionState: Max");
                break;
            default:
                break;
        }
    }
    void Pc2OnIceConnectionChange(RTCIceConnectionState state)
    {
        OnIceConnectionChange(pc2, state);
    }

    void Pc2OnIceCandidate(RTCIceCandidate candidate)
    {
        OnIceCandidate(pc2, candidate);
    }

    IEnumerator Call()
    {
        phase = 2;
        Debug.Log("GetSelectedSdpSemantics");
        var configuration = GetSelectedSdpSemantics();
        pc2 = new RTCPeerConnection(ref configuration);
        //Debug.Log("Created remote peer connection object pc2");
        pc2.OnIceCandidate = pc2OnIceCandidate;
        pc2.OnIceConnectionChange = pc2OnIceConnectionChange;
        pc2.OnDataChannel = onDataChannel;
        RTCSessionDescription desc;
        desc.sdp = remoteDescription;
        desc.type = RTCSdpType.Offer;
        //Debug.Log(desc.sdp);
        //Debug.Log(desc.type);
        pc2.SetRemoteDescription(ref desc);
        RTCDataChannelInit conf = new RTCDataChannelInit();

        Debug.Log("pc2 createAnswer start");
        var op = pc2.CreateAnswer();
        yield return op;


        if (!op.IsError)
        {
            Debug.Log(op.Desc.sdp);
            yield return (StartCoroutine(OnCreateAnswerSuccess(op.Desc)));
        }
        else
        {
            OnCreateSessionDescriptionError(op.Error);
        }
    }

    void Hangup()
    {

        pc2.Close();
        pc1 = null;
        pc2 = null;
    }


    /// <summary>
    ///
    /// </summary>
    /// <param name="pc"></param>
    /// <param name="streamEvent"></param>
    void OnIceCandidate(RTCPeerConnection pc, RTCIceCandidate candidate)
    {
        //GetOtherPc(pc).AddIceCandidate(candidate);

        Debug.Log(candidate.Address);
        Debug.Log(candidate.Candidate);

        localCandidate = candidate.Candidate;

        //Debug.Log($"{GetName(pc)} ICE candidate:\n {candidate.Candidate}");
    }

    public void SendMsg()
    {

    }
    string GetName(RTCPeerConnection pc)
    {
        return (pc == pc1) ? "pc1" : "pc2";
    }

    RTCPeerConnection GetOtherPc(RTCPeerConnection pc)
    {
        return (pc == pc1) ? pc2 : pc1;
    }


    void OnSetLocalSuccess(RTCPeerConnection pc)
    {
        ws.Send("d" + localDescription);
        Debug.Log($"{GetName(pc)} SetLocalDescription complete");
    }

    void OnSetSessionDescriptionError(ref RTCError error) { }

    void OnSetRemoteSuccess(RTCPeerConnection pc)
    {
        Debug.Log($"{GetName(pc)} SetRemoteDescription complete");
    }

    IEnumerator OnCreateAnswerSuccess(RTCSessionDescription desc)
    {
        //Debug.Log($"Answer from pc2:\n{desc.sdp}");
        //Debug.Log("pc2 setLocalDescription start");
        localDescription = desc.sdp;
        var op = pc2.SetLocalDescription(ref desc);
        yield return op;

        if (!op.IsError)
        {
            OnSetLocalSuccess(pc2);
        }
        else
        {
            var error = op.Error;
            OnSetSessionDescriptionError(ref error);
        }
    }

    IEnumerator LoopGetStats()
    {
        while (true)
        {
            yield return new WaitForSeconds(1f);


            var op1 = pc1.GetStats();
            var op2 = pc2.GetStats();

            yield return op1;
            yield return op2;

            Debug.Log("pc1");
            foreach (var stat in op1.Value.Stats.Values)
            {
                Debug.Log(stat.Type.ToString());
            }
            Debug.Log("pc2");
            foreach (var stat in op2.Value.Stats.Values)
            {
                Debug.Log(stat.Type.ToString());
            }
        }
    }

    void OnAddIceCandidateSuccess(RTCPeerConnection pc)
    {
        Debug.Log($"{GetName(pc)} addIceCandidate success");
    }

    void OnAddIceCandidateError(RTCPeerConnection pc, RTCError error)
    {
        Debug.Log($"{GetName(pc)} failed to add ICE Candidate: ${error}");
    }

    void OnCreateSessionDescriptionError(RTCError e)
    {
        Debug.Log(e.message);
    }
}