using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityToolbag;
using static LithFAQ.LTTypes;
using static LithFAQ.LTUtils;
using static Utility.MaterialSafeMeshCombine;
using static DTX;
using TMPro;
using System.Xml.Linq;



namespace LithFAQ
{
    public class DATReader70 : MonoBehaviour, IDATReader
    {
        public WorldObjects LTGameObjects = new WorldObjects();
        WorldReader worldReader = new WorldReader();
        List<WorldBsp> bspListTest = new List<WorldBsp>();

        public float UNITYSCALEFACTOR = 0.01f; //default scale to fit in Unity's world.
        public Importer importer;

        public ABCModelReader abc = new ABCModelReader();

        public void Start()
        {
            importer = GetComponent<Importer>();
            gameObject.AddComponent<Dispatcher>();
        }

        public void ClearLevel()
        {
            //reset loading text
            importer.loadingUI.text = "LOADING...";

            GameObject go = GameObject.Find("Level");

            //destroy all Meshes under the Level object
            MeshFilter[] meshFilters = go.GetComponentsInChildren<MeshFilter>();
            foreach (MeshFilter meshFilter in meshFilters)
            {
                DestroyImmediate(meshFilter.sharedMesh);
            }

            foreach (Transform child in go.transform)
            {
                Destroy(child.gameObject);
            }

            go = GameObject.Find("objects");

            foreach (Transform child in go.transform)
            {
                Destroy(child.gameObject);
            }

            //find all objects named New Game Object
            GameObject[] newGameObjects = GameObject.FindObjectsOfType<GameObject>();

            //TODO: FIX THE ISSUE WHERE NEW GAMES OBJECTS ARE BEING CREATED IN ROOT OF SCENE.
            // List or use the found objects
            foreach (var obj in newGameObjects)
            {
                if (obj.name == "New Game Object")
                {
                    // Do something with the object
                    Destroy(obj);
                }
            }

            worldReader = new WorldReader();
            bspListTest = new List<WorldBsp>();
            LTGameObjects = new WorldObjects();

            foreach (Texture2D tex in importer.dtxMaterialList.textures.Values)
            {
                DestroyImmediate(tex);
            }
            foreach (Material mat in importer.dtxMaterialList.materials.Values)
            {
                DestroyImmediate(mat);
            }

            importer.dtxMaterialList = null;
            importer.dtxMaterialList = new DTX.DTXMaterial();

            Resources.UnloadUnusedAssets();

            //reset UI
            Controller lightController = GetComponent<Controller>();

            foreach (var toggle in lightController.settingsToggleList)
            {
                toggle.isOn = true;

                if (toggle.name == "Shadows")
                    toggle.isOn = false;
            }
        }

        public void Load(BinaryReader b)
        {
            importer = gameObject.GetComponent<Importer>();

            ClearLevel();

            LoadLevel(b);
        }

        public async void LoadLevel(BinaryReader b)
        {
            importer.loadingUI.enabled = true;
            await System.Threading.Tasks.Task.Yield();

            worldReader.ReadHeader(ref b);
            worldReader.ReadPropertiesAndExtents(ref b);

            WorldTree wTree = new WorldTree();

            wTree.ReadWorldTree(ref b);

            //read world models...
            byte[] anDummy = new byte[32];
            int nNextWMPosition = 0;

            WorldData pWorldData = new WorldData();

            WorldModelList WMList = new WorldModelList();
            WMList.pModelList = new List<WorldData>();
            WMList.nNumModels = b.ReadInt32();

            for (int i = 0; i < WMList.nNumModels; i++)
            {
                nNextWMPosition = b.ReadInt32();
                anDummy = b.ReadBytes(anDummy.Length);

                pWorldData.NextPos = nNextWMPosition;
                WMList.pModelList.Add(pWorldData);

                WorldBsp tBSP = new WorldBsp();
                tBSP.datVersion = worldReader.WorldHeader.nVersion;

                try
                {
                    tBSP.Load(ref b, true);
                    bspListTest.Add(tBSP);
                }
                catch (Exception e)
                {
                    Debug.Log(e.Message);
                }

                b.BaseStream.Position = nNextWMPosition;
            }

            b.BaseStream.Position = worldReader.WorldHeader.dwObjectDataPos;
            LoadObjects(ref b);

            importer.infoBox.text = string.Format("Loaded World: {0}", Path.GetFileName(importer.fileName));

            b.BaseStream.Close();

            importer.loadingUI.text = "Loading Objects";
            await System.Threading.Tasks.Task.Yield();

            int id = 0;
            foreach (WorldBsp tBSP in bspListTest)
            {
                if (tBSP.m_szWorldName.Contains("PhysicsBSP"))
                {
                    importer.loadingUI.text = "Loading BSP";
                    await System.Threading.Tasks.Task.Yield();
                }

                if (tBSP.m_szWorldName != "VisBSP")
                {
                    bool isPartOfObject = !tBSP.m_szWorldName.Contains("PhysicsBSP");

                    GameObject mainObject = new GameObject(tBSP.WorldName);
                    mainObject.transform.parent = this.transform;
                    mainObject.AddComponent<MeshFilter>();
                    mainObject.AddComponent<MeshRenderer>().material = importer.defaultMaterial;

                    if (tBSP.m_aszTextureNames[0].Contains("AI.dtx") ||
                        tBSP.m_szWorldName.Contains("Volume") ||
                        tBSP.m_szWorldName.Contains("Water") ||
                        tBSP.m_szWorldName.Contains("weather") ||
                        tBSP.m_szWorldName.Contains("Weather") ||
                        tBSP.m_szWorldName.Contains("Ladder"))
                    {
                        mainObject.tag = "Volumes";
                    }

                    LoadTexturesForBSP(tBSP);

                    foreach (WorldPoly tPoly in tBSP.m_pPolies)
                    {
                        //remove all bsp invisible
                        if (tBSP.m_aszTextureNames[tPoly.GetSurface(tBSP).m_nTexture].Contains("Invisible", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        float texWidth = 256f;
                        float texHeight = 256f;

                        string szTextureName = Path.GetFileName(tBSP.m_aszTextureNames[tBSP.m_pSurfaces[tPoly.m_nSurface].m_nTexture]);

                        //skip sky portals
                        if ((tPoly.GetSurface(tBSP).m_nFlags & (int)BitMask.SKY) == (int)BitMask.SKY)
                        {
                            continue;
                        }

                        SetLithTechInternalTextureSize(ref texWidth, ref texHeight, szTextureName);

                        //Convert OPQ to UV magic
                        Vector3 center = tPoly.m_vCenter;

                        Vector3 o = tPoly.m_O;
                        Vector3 p = tPoly.m_P;
                        Vector3 q = tPoly.m_Q;

                        o *= UNITYSCALEFACTOR;
                        o -= (Vector3)tPoly.m_vCenter;
                        p /= UNITYSCALEFACTOR;
                        q /= UNITYSCALEFACTOR;

                        Material matReference = importer.defaultMaterial;

                        foreach (var mats in importer.dtxMaterialList.materials.Keys)
                        {
                            if (mats.Contains(szTextureName))
                            {
                                matReference = importer.dtxMaterialList.materials[szTextureName];
                            }
                        }

                        var possibleTWM = GameObject.Find(tBSP.WorldName + "_obj");

                        if (possibleTWM)
                        {

                            if (szTextureName.Contains("invisible", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            var twm = possibleTWM.GetComponent<TranslucentWorldModel>();
                            if (twm)
                            {
                                if (twm.bChromakey || (tPoly.GetSurface(tBSP).m_nFlags & (int)BitMask.TRANSLUCENT) == (int)BitMask.TRANSLUCENT)
                                {
                                    //try to find already existing material
                                    if (importer.dtxMaterialList.materials.ContainsKey(matReference.name + "_Chromakey"))
                                    {
                                        matReference = importer.dtxMaterialList.materials[matReference.name + "_Chromakey"];
                                    }
                                    else
                                    {
                                        //copy material from matReference to a new
                                        Material mat = new Material(Shader.Find("Shader Graphs/Lithtech Vertex Transparent"));
                                        mat.name = matReference.name + "_Chromakey";
                                        mat.mainTexture = matReference.mainTexture;
                                        mat.SetInt("_Chromakey", 1);
                                        matReference = mat;
                                        AddMaterialToMaterialDictionary(mat.name, mat, importer.dtxMaterialList);
                                    }
                                }

                                if ((tPoly.GetSurface(tBSP).m_nFlags & (int)BitMask.INVISIBLE) == (int)BitMask.INVISIBLE)
                                {
                                    mainObject.tag = "Blocker";
                                }
                                if (!twm.bVisible)
                                {
                                    mainObject.tag = "Blocker";
                                }
                                
                            }
                        }

                        // CALCULATE EACH TRI INDIVIDUALLY.
                        for (int nTriIndex = 0; nTriIndex < tPoly.m_nLoVerts - 2; nTriIndex++)
                        {
                            List<Vector3> vertexList = new List<Vector3>();
                            List<Vector3> _aVertexNormalList = new List<Vector3>();
                            List<Color> _aVertexColorList = new List<Color>();
                            List<Vector2> _aUVList = new List<Vector2>();
                            List<int> _aTriangleIndices = new List<int>();

                            GameObject go = new GameObject(tBSP.WorldName + id);
                            go.transform.parent = mainObject.transform;
                            MeshRenderer mr = go.AddComponent<MeshRenderer>();
                            MeshFilter mf = go.AddComponent<MeshFilter>();

                            Mesh m = new Mesh();

                            // Do the thing
                            for (int vCount = 0; vCount < tPoly.m_nLoVerts; vCount++)
                            {
                                WorldVertex tVertex = tBSP.m_pPoints[tPoly.m_aVertexColorList[vCount].nVerts];

                                Vector3 data = tVertex.m_vData;
                                data *= UNITYSCALEFACTOR;
                                vertexList.Add(data);

                                Color color = new Color(tPoly.m_aVertexColorList[vCount].red / 255, tPoly.m_aVertexColorList[vCount].green / 255, tPoly.m_aVertexColorList[vCount].blue / 255, 1.0f);
                                _aVertexColorList.Add(color);

                                _aVertexNormalList.Add(tBSP.m_pPlanes[tPoly.m_nPlane].m_vNormal);

                                // Calculate UV coordinates based on the OPQ vectors
                                // Note that since the worlds are offset from 0,0,0 sometimes we need to subtract the center point
                                Vector3 curVert = vertexList[vCount];
                                float u = Vector3.Dot((curVert - center) - o, p);
                                float v = Vector3.Dot((curVert - center) - o, q);

                                //Scale back down into something more sane
                                u /= texWidth;
                                v /= texHeight;

                                _aUVList.Add(new Vector2(u, v));
                            }

                            m.SetVertices(vertexList);
                            m.SetNormals(_aVertexNormalList);
                            m.SetUVs(0, _aUVList);
                            m.SetColors(_aVertexColorList);

                            //Hacky, whatever
                            _aTriangleIndices.Add(0);
                            _aTriangleIndices.Add(nTriIndex + 1);
                            _aTriangleIndices.Add((nTriIndex + 2) % tPoly.m_nLoVerts);

                            // Set triangles
                            m.SetTriangles(_aTriangleIndices, 0);
                            m.RecalculateTangents();

                            mr.material = matReference;
                            mf.mesh = m;
                            mf.mesh = m;

                            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;

                        }
                        id++;
                    }
                }
            }


            //find reflection probe and update it
            //var reflectionProbe = GameObject.Find("Main Camera").GetComponent<ReflectionProbe>().RenderProbe();

            importer.loadingUI.text = "Combining Meshes";
            //yield return new WaitForEndOfFrame();

            //combine all meshes not named PhysicsBSP
            List<GameObject> twmToBind = new List<GameObject>();
            foreach (var t in GameObject.Find("Level").gameObject.GetComponentsInChildren<MeshFilter>())
            {
                if (t.transform.gameObject.name != "PhysicsBSP")
                {
                    t.gameObject.MeshCombine(true);
                }
            }


            var g = GameObject.Find("PhysicsBSP");
            Mesh[] meshes = g.GetComponentsInChildren<MeshFilter>().Select(mf => mf.sharedMesh).ToArray();

            g.MeshCombine(true);

            //after mesh combine, we need to recalculate the normals
            MeshFilter[] meshFilters = g.GetComponentsInChildren<MeshFilter>();
            foreach (MeshFilter mf in meshFilters)
            {

                mf.mesh.Optimize();
                mf.mesh.RecalculateNormals();
                mf.mesh.RecalculateTangents();
            }


            var twmToAdd = GameObject.Find("Level");
            foreach (var t in twmToAdd.gameObject.GetComponentsInChildren<MeshFilter>())
            {
                var mc = t.transform.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = t.mesh;
                //mc.convex = true;
            }

            foreach (var t in twmToAdd.gameObject.GetComponentsInChildren<MeshRenderer>())
            {
                t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            }

            //Loop through all objects under Level and add MeshFilter to a list
            List<GameObject> toBatch = new List<GameObject>();

            foreach (Transform child in GameObject.Find("Level").transform)
            {
                if (child.gameObject.GetComponent<MeshFilter>() != null)
                {
                    toBatch.Add(child.gameObject);
                }
            }

            importer.loadingUI.enabled = false;

            //Batch all the objects
            StaticBatchingUtility.Combine(toBatch.ToArray(), GameObject.Find("Level"));
            await System.Threading.Tasks.Task.Yield();
        }

        private void LoadTexturesForBSP(WorldBsp tBSP)
        {
            //Load texture
            foreach (var tex in tBSP.m_aszTextureNames)
            {
                DTX.LoadDTX(importer.projectPath + "\\" + tex, ref importer.dtxMaterialList, importer.projectPath);
            }
        }

        private void SetLithTechInternalTextureSize(ref float texWidth, ref float texHeight, string szTextureName)
        {
            //Lookup the width and height the engine uses to calculate UV's
            //UI Mipmap Offset changes this
            foreach (var mats in importer.dtxMaterialList.materials.Keys)
            {
                if (mats.Contains(szTextureName))
                {
                    texWidth = importer.dtxMaterialList.texSize[szTextureName].engineWidth;
                    texHeight = importer.dtxMaterialList.texSize[szTextureName].engineHeight;
                }
            }
        }

        IEnumerator LoadAndPlay(string uri, AudioSource audioSource)
        {
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogError("Error: " + www.error);
                }
                else
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    audioSource.clip = clip;
                    audioSource.Play();
                }
            }
        }


        public void LoadObjects(ref BinaryReader b)
        {

            LTGameObjects = ReadObjects(ref b);

            foreach (var obj in LTGameObjects.obj)
            {
                Vector3 objectPos = new Vector3();
                Quaternion objectRot = new Quaternion();
                Vector3 rot = new Vector3();
                String objectName = String.Empty;
                bool bInvisible = false;
                bool bChromakey = false;

                foreach (var subItem in obj.options)
                {
                    if (subItem.Key == "Name")
                        objectName = (String)subItem.Value;

                    else if (subItem.Key == "Pos")
                    {
                        LTVector temp = (LTVector)subItem.Value;
                        objectPos = new Vector3(temp.X, temp.Y, temp.Z) * UNITYSCALEFACTOR;
                    }

                    else if (subItem.Key == "Rotation")
                    {
                        LTRotation temp = (LTRotation)subItem.Value;
                        rot = new Vector3(temp.X * Mathf.Rad2Deg, temp.Y * Mathf.Rad2Deg, temp.Z * Mathf.Rad2Deg);
                    }

                }

                var tempObject = Instantiate(importer.RuntimeGizmoPrefab, objectPos, objectRot);
                tempObject.name = objectName + "_obj";
                tempObject.transform.eulerAngles = rot;
                
                if(obj.objectName == "WorldProperties")
                {
                    //find child gameobject named Icon
                    var icon = tempObject.transform.Find("Icon");
                    icon.GetComponent<MeshRenderer>().material.mainTexture = Resources.Load<Texture2D>("Gizmos/worldproperties");
                }

                if (obj.objectName == "SoundFX")
                {
                    //find child gameobject named Icon
                    var icon = tempObject.transform.Find("Icon");
                    icon.GetComponent<MeshRenderer>().material.mainTexture = Resources.Load<Texture2D>("Gizmos/sound");

                    AudioSource temp = tempObject.AddComponent<AudioSource>();
                    var volumeControl = tempObject.AddComponent<Volume2D>();

                    string szFilePath = String.Empty;

                    foreach (var subItem in obj.options)
                    {

                        if (subItem.Key == "Sound")
                        {
                            szFilePath = importer.projectPath + "\\" + subItem.Value;
                        }

                        if (subItem.Key == "Loop")
                        {
                            temp.loop = (bool)subItem.Value;
                        }

                        if (subItem.Key == "Ambient")
                        {
                            if ((bool)subItem.Value)
                            {
                                temp.spatialize = false;
                            }
                            else
                            {
                                temp.spatialize = true;
                                temp.spatialBlend = 1.0f;

                            }
                        }

                        if (subItem.Key == "Volume")
                        {
                            float vol = (float)(Int64)subItem.Value;
                            temp.volume = vol / 100;
                        }
                        if (subItem.Key == "OuterRadius")
                        {
                            float vol = (float)subItem.Value;
                            temp.maxDistance = vol / 75;

                            volumeControl.audioSource = temp;
                            volumeControl.listenerTransform = Camera.main.transform;
                            volumeControl.maxDist = temp.maxDistance;
                        }

                    }
                    StartCoroutine(LoadAndPlay(szFilePath, temp));
                }

                if (obj.objectName == "TranslucentWorldModel" || obj.objectName == "Electricity")
                {
                    string szObjectName = String.Empty;
                    foreach (var subItem in obj.options)
                    {
                        if (subItem.Key == "Visible")
                            bInvisible = (bool)subItem.Value;

                        else if (subItem.Key == "Chromakey")
                            bChromakey = (bool)subItem.Value;
                        else if (subItem.Key == "Name")
                            szObjectName = (String)subItem.Value;
                    }

                    var twm = tempObject.AddComponent<TranslucentWorldModel>();
                    twm.bChromakey = bChromakey;
                    twm.bVisible = bInvisible;
                    twm.szName = szObjectName;
                }


                if (obj.objectName == "Light")
                {
                    //find child gameobject named Icon
                    var icon = tempObject.transform.Find("Icon");
                    icon.GetComponent<MeshRenderer>().material.mainTexture = Resources.Load<Texture2D>("Gizmos/light");
                    
                    var light = tempObject.gameObject.AddComponent<Light>();


                    foreach (var subItem in obj.options)
                    {
                        if (subItem.Key == "LightRadius")
                            light.range = (float)subItem.Value * 0.20f;

                        else if (subItem.Key == "LightColor")
                        {
                            var vec = (LTVector)subItem.Value;
                            Vector3 col = Vector3.Normalize(new Vector3(vec.X, vec.Y, vec.Z));
                            light.color = new Color(col.x, col.y, col.z);
                        }

                        else if (subItem.Key == "BrightScale")
                            light.intensity = (float)subItem.Value * 0.75f;
                    }
                    light.shadows = LightShadows.Soft;

                    Controller lightController = transform.GetComponent<Controller>();

                    foreach (var toggle in lightController.settingsToggleList)
                    {
                        if (toggle.name == "Shadows")
                        {
                            if (toggle.isOn)
                                light.shadows = LightShadows.Soft;
                            else
                                light.shadows = LightShadows.None;
                        }
                    }
                }

                if (obj.objectName == "DirLight")
                {
                    //find child gameobject named Icon
                    var icon = tempObject.transform.Find("Icon");
                    icon.GetComponent<MeshRenderer>().material.mainTexture = Resources.Load<Texture2D>("Gizmos/light");
                    var light = tempObject.gameObject.AddComponent<Light>();


                    foreach (var subItem in obj.options)
                    {
                        if (subItem.Key == "FOV")
                            light.spotAngle = (float)subItem.Value;

                        else if (subItem.Key == "LightRadius")
                            light.range = (float)subItem.Value * 0.025f;

                        else if (subItem.Key == "InnerColor")
                        {
                            var vec = (LTVector)subItem.Value;
                            Vector3 col = Vector3.Normalize(new Vector3(vec.X, vec.Y, vec.Z));
                            light.color = new Color(col.x, col.y, col.z);
                        }

                        else if (subItem.Key == "BrightScale")
                            light.intensity = (float)subItem.Value * 0.65f;
                    }

                    light.shadows = LightShadows.Soft;
                    light.type = LightType.Spot;

                    Controller lightController = GetComponent<Controller>();

                    foreach (var toggle in lightController.settingsToggleList)
                    {
                        if (toggle.name == "Shadows")
                        {
                            if (toggle.isOn)
                                light.shadows = LightShadows.Soft;
                            else
                                light.shadows = LightShadows.None;
                        }
                    }
                }

                if (obj.objectName == "StaticSunLight")
                {
                    //find child gameobject named Icon
                    var icon = tempObject.transform.Find("Icon");
                    icon.GetComponent<MeshRenderer>().material.mainTexture = Resources.Load<Texture2D>("Gizmos/light");
                    var light = tempObject.gameObject.AddComponent<Light>();

                    foreach (var subItem in obj.options)
                    {
                        if (subItem.Key == "InnerColor")
                        {
                            var vec = (LTVector)subItem.Value;
                            Vector3 col = Vector3.Normalize(new Vector3(vec.X, vec.Y, vec.Z));
                            light.color = new Color(col.x, col.y, col.z);
                        }
                        else if (subItem.Key == "BrightScale")
                            light.intensity = (float)subItem.Value * 0.65f;
                    }

                    light.shadows = LightShadows.Soft;
                    light.type = LightType.Directional;

                    Controller lightController = GetComponent<Controller>();

                    foreach (var toggle in lightController.settingsToggleList)
                    {
                        if (toggle.name == "Shadows")
                        {
                            if (toggle.isOn)
                                light.shadows = LightShadows.Soft;
                            else
                                light.shadows = LightShadows.None;
                        }
                    }
                }

                if(obj.objectName == "GameStartPoint")
                {

                    int nCount = ModelDefinition.AVP2RandomCharacterGameStartPoint.Length;

                    int nRandom = UnityEngine.Random.Range(0, nCount);
                    string szName = ModelDefinition.AVP2RandomCharacterGameStartPoint[nRandom];

                    var temp = importer.CreateModelDefinition(szName, ModelType.Character);

                    
                    var gos = abc.LoadABC(temp);

                    gos.transform.position = tempObject.transform.position;
                    gos.transform.eulerAngles = rot;

                    //find child gameobject named Icon
                    var icon = tempObject.transform.Find("Icon");
                    icon.GetComponent<MeshRenderer>().material.mainTexture = Resources.Load<Texture2D>("Gizmos/gsp");
                }

                if(obj.objectName == "PickupObject")
                {
                    string szName = "";

                    if (obj.options.ContainsKey("Pickup"))
                    {
                        szName = (string)obj.options["Pickup"];
                    }

                    //abc.FromFile("Assets/Models/" + szName + ".abc", true);

                    var temp = importer.CreateModelDefinition(szName, ModelType.Pickup);

                    var gos = abc.LoadABC(temp);

                    gos.transform.position = tempObject.transform.position;
                    gos.transform.eulerAngles = rot;

                    //move to floort with raycast
                    RaycastHit hit;
                    if (Physics.Raycast(gos.transform.position, Vector3.down, out hit, 1000))
                    {
                        gos.transform.position = hit.point;
                    }


                }

                if(obj.objectName == "Prop" || 
                    obj.objectName == "AmmoBox" ||
                    obj.objectName == "Beetle" ||
                    obj.objectName == "BodyProp" ||
                    obj.objectName == "Civilian" ||
                    obj.objectName == "Egg" ||
                    obj.objectName == "HackableLock" ||
                    obj.objectName == "Plant" ||
                    obj.objectName == "StoryObject" ||
                    obj.objectName == "MEMO" ||
                    obj.objectName == "PC" ||
                    obj.objectName == "PDA" ||
                    obj.objectName == "Striker" ||
                    obj.objectName == "TorchableLock" ||
                    obj.objectName == "Turret"
                    )
                {

                    string szName = "";

                    if (obj.options.ContainsKey("Name"))
                    {
                        szName = (string)obj.options["Name"];
                    }

                    var temp = importer.CreateModelDefinition(szName, ModelType.Prop, obj.options);

                    var gos = abc.LoadABC(temp);

                    gos.transform.position = tempObject.transform.position;
                    gos.transform.eulerAngles = rot;
                }

                var g = GameObject.Find("objects");
                tempObject.transform.SetParent(g.transform);
                g.transform.localScale = Vector3.one;


                
                //var rtObj = newGO.AddComponent<RuntimeObjectType>();
                //rtObj.cam = Camera.main.transform;
                //rtObj.objectType = tempObject.name;
            }


            //disable unity's nastyness
            //RenderSettings.ambientLight = Color.black;
            //RenderSettings.ambientIntensity = 0.0f;

            //Setup AmbientLight
            SetupAmbientLight();
        }
        public void SetupAmbientLight()
        {
            if (worldReader.WorldProperties != null)
            {
                var worldPropertiesArray = worldReader.WorldProperties.Split(';');

                foreach (var property in worldPropertiesArray)
                {
                    if (property.Contains("AmbientLight"))
                    {

                        string property2 = String.Empty;
                        int ambPos = property.IndexOf("AmbientLight");
                        int startPos = property.IndexOf(" ");

                        int endPos = property.IndexOf(";");

                        if (endPos == -1)
                            endPos = property.Length;

                        property2 = property;
                        if (startPos == 0)
                        {
                            property2 = property.Substring(startPos + 1, endPos - 1 - startPos);
                            startPos = property2.IndexOf(" ");
                            endPos = property2.Length;
                        }

                        var szTemp = property2.Substring(startPos + 1, endPos - 1 - startPos);

                        var splitStrings = szTemp.Split(' ');

                        Vector3 vAmbientRGB = Vector3.Normalize(new Vector3(
                            float.Parse(splitStrings[0]),
                            float.Parse(splitStrings[1]),
                            float.Parse(splitStrings[2])
                        ));

                        var color = new Color(vAmbientRGB.x, vAmbientRGB.y, vAmbientRGB.y, 255);
                        RenderSettings.ambientLight = color;
                        importer.defaultColor = color;

                        //Check if color is 0,0,0 and boost a bit
                        if (color.r == 0 && color.g == 0 && color.b == 0)
                        {
                            RenderSettings.ambientLight = new Color(0.1f, 0.1f, 0.1f, 255);
                            importer.defaultColor = new Color(0.1f, 0.1f, 0.1f, 255);
                        }
                        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                        RenderSettings.ambientIntensity = 1.0f;
                    }
                    else
                    {
                        importer.defaultColor = new Color(0.1f, 0.1f, 0.1f, 255);
                        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                        RenderSettings.ambientIntensity = 1.0f;
                    }
                }
            }
        }
        public void Quit()
        {
            Application.Quit();
        }

        public static WorldObjects ReadObjects(ref BinaryReader b)
        {
            WorldObjects temp = new WorldObjects();
            temp.obj = new List<WorldObject>();

            var totalObjectCount = b.ReadInt32();

            for (int i = 0; i < totalObjectCount; i++)
            {
                //Make a new object
                WorldObject tempObject = new WorldObject();

                //Make a dictionary to make things easier
                Dictionary<string, object> tempData = new Dictionary<string, object>();

                tempObject.dataLength = b.ReadInt16(); // Read our object datalength

                var dataLength = b.ReadInt16(); //read out property length

                tempObject.objectName = ReadString(dataLength, ref b); // read our name

                tempObject.objectEntries = b.ReadInt16();// read how many properties this object has

                b.BaseStream.Position += 2;

                for (int t = 0; t < tempObject.objectEntries; t++)
                {

                    var tempDataLength = b.ReadInt16();
                    string propertyName = ReadString(tempDataLength, ref b);

                    byte propType = b.ReadByte();

                    switch (propType)
                    {
                        case (byte)PropType.PT_STRING:
                            b.BaseStream.Position += 6; // skip property flags;
                                                        //Get Data Length
                            tempDataLength = b.ReadInt16();
                            //Read the string
                            tempData.Add(propertyName, ReadString(tempDataLength, ref b));
                            break;

                        case (byte)PropType.PT_VECTOR:
                            b.BaseStream.Position += 4; // skip property flags;
                                                        //Get our data length
                            tempDataLength = b.ReadInt16();
                            //Get our float data
                            LTVector tempVec = ReadLTVector(ref b);
                            //Add our object to the Dictionary
                            tempData.Add(propertyName, tempVec);
                            break;

                        case (byte)PropType.PT_ROTATION:
                            b.BaseStream.Position += 4; // skip property flags;
                                                        //Get our data length
                            tempDataLength = b.ReadInt16();
                            //Get our float data
                            LTRotation tempRot = ReadLTRotation(ref b);
                            //Add our object to the Dictionary
                            tempData.Add(propertyName, tempRot);
                            break;
                        case (byte)PropType.PT_LONGINT:
                            b.BaseStream.Position += 2; // skip property flags;
                                                        //Get our data length
                            Int64 longInt = ReadLongInt(ref b);
                            //b.BaseStream.Position += 4;
                            //b.BaseStream.Position += 2;
                            //Add our object to the Dictionary
                            tempData.Add(propertyName, longInt);
                            break;
                        case (byte)PropType.PT_BOOL:
                            b.BaseStream.Position += 6; // skip property flags;
                                                        //Add our object to the Dictionary
                            tempData.Add(propertyName, ReadBool(ref b));
                            break;
                        case (byte)PropType.PT_REAL:
                            b.BaseStream.Position += 4; // skip property flags;
                                                        //Get our data length
                            tempDataLength = b.ReadInt16();
                            //Add our object to the Dictionary
                            tempData.Add(propertyName, ReadReal(ref b));
                            break;
                        case (byte)PropType.PT_COLOR:
                            b.BaseStream.Position += 4; // skip property flags;
                                                        //Get our data length
                            tempDataLength = b.ReadInt16();
                            //Get our float data
                            LTVector tempCol = ReadLTVector(ref b);
                            //Add our object to the Dictionary
                            tempData.Add(propertyName, tempCol);
                            break;
                    }
                }

                tempObject.options = tempData;

                temp.obj.Add(tempObject);
            }
            return temp;
        }
    }
}