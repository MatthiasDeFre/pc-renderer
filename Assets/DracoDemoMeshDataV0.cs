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

using System.IO;
using UnityEngine;
using Draco;
using System.Linq;
using Unity.Collections;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class DracoDemoMeshDataV : MonoBehaviour {
    
    public string filePath;

    public bool requireNormals;
    public bool requireTangents;
    
    async void Start() {
        
        var data = File.ReadAllBytes("longdress.drc");
        if (data == null) return;

        // Convert data to Unity mesh
        var draco = new DracoMeshLoader();

        // Allocate single mesh data (you can/should bulk allocate multiple at once, if you're loading multiple draco meshes) 
        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        
        // Async decoding has to start on the main thread and spawns multiple C# jobs.
        var result = await draco.ConvertDracoMeshToUnity(
            meshDataArray[0],
            data,
            requireNormals, // Set to true if you require normals. If Draco data does not contain them, they are allocated and we have to calculate them below
            requireTangents // Retrieve tangents is not supported, but this will ensure they are allocated and can be calculated later (see below)
            );
        
        if (result.success) {
            
            // Apply onto new Mesh
            var mesh = new Mesh();
            //Mesh.ApplyAndDisposeWritableMeshData(meshDataArray,mesh);

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
            pos.Dispose();
            // Use the resulting mesh
           // Debug.Log(mesh.GetTopology());
        }
    }
}