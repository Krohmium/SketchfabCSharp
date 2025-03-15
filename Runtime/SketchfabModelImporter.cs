﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using GLTFast;
using System.Threading.Tasks;
using GLTFast.Schema;
using UnityEditor;
using Material = UnityEngine.Material;

public static class SketchfabModelImporter
{
    private const string m_SketchfabModelCacheFolderName = "SketchfabModelCache";
    private const string m_SketchfabModelTemporaryDownloadFolderName = "SketchfabModelTemp";

    private static SketchfabModelDiskCache m_Cache = new SketchfabModelDiskCache(Path.Combine(Application.persistentDataPath, m_SketchfabModelCacheFolderName), 1024 * 1024 * 1024);
    private static SketchfabModelDiskTemp m_Temp = new SketchfabModelDiskTemp(Path.Combine(Application.persistentDataPath, m_SketchfabModelTemporaryDownloadFolderName), 10.0f);

    public static void EnsureInitialized()
    {
        if (m_Cache == null)
        {
            m_Cache = new SketchfabModelDiskCache(Path.Combine(Application.persistentDataPath, m_SketchfabModelCacheFolderName), 1024 * 1024 * 1024);
            m_Temp = m_Temp = new SketchfabModelDiskTemp(Path.Combine(Application.persistentDataPath, m_SketchfabModelTemporaryDownloadFolderName), 5.0f);
        }
    }

    public static async void Import(SketchfabModel _model, Action<GameObject> _onModelImported, bool _enableCache = false)
    {
        if (_enableCache)
        {
            if (await m_Cache.IsInCache(_model))
            {
                GltfImport($"file://{Path.Combine(m_Cache.AbsolutePath, _model.Uid, "scene.gltf")}", _onModelImported);
                return;
            }
        }

        SketchfabAPI.GetGLTFModelDownloadUrl(_model.Uid, (SketchfabResponse<string> _modelDownloadUrl) =>
        {
            if (!_modelDownloadUrl.Success)
            {
                Debug.LogError(_modelDownloadUrl.ErrorMessage);

                _onModelImported?.Invoke(null);

                return;
            }

            Import(_model, _modelDownloadUrl.Object, _onModelImported);
        });
    }

    private static void Import(SketchfabModel _model, string _downloadUrl, Action<GameObject> _onModelImported)
    {
        UnityWebRequest downloadRequest = UnityWebRequest.Get(_downloadUrl);

        SketchfabWebRequestManager.Instance.SendRequest(downloadRequest, (UnityWebRequest _request) =>
        {
            if (downloadRequest.result == UnityWebRequest.Result.ProtocolError ||
                downloadRequest.result == UnityWebRequest.Result.ConnectionError)
            {
                Debug.Log(downloadRequest.error);

                _onModelImported?.Invoke(null);

                return;
            }

            // Lock the temporary folder for all following operations to
            // avoid it from flushing itself in the middle of it
            m_Temp.Lock();

            try
            {
                string archivePath = Path.Combine(m_Temp.AbsolutePath, _model.Uid);
                // Make sure to save again the model if downloaded twice
                if (Directory.Exists(archivePath))
                {
                    Directory.Delete(archivePath, true);
                }

                using (ZipArchive zipArchive = new ZipArchive(new MemoryStream(downloadRequest.downloadHandler.data), ZipArchiveMode.Read))
                {
                    zipArchive.ExtractToDirectory(archivePath);
                }


                SaveModelMetadata(archivePath, _model);
                GltfImport($"file://{Path.Combine(archivePath, "scene.gltf")}", (GameObject _importedModel) =>
                {
                    DirectoryInfo gltfDirectoryInfo = new DirectoryInfo(archivePath);
                    m_Cache.AddToCache(gltfDirectoryInfo);

                    _onModelImported?.Invoke(_importedModel);
                });
            }
            finally
            {
                // No matter what happens, realse the lock so that
                // it doesn't get stuck
                m_Temp.Unlock();
            }
        });
    }

    private static void SaveModelMetadata(string _destination, SketchfabModel _model)
    {
        // Write the model metadata in order to avoid server queries
        File.WriteAllText(Path.Combine(_destination, string.Format("{0}_metadata.json", _model.Uid)), _model.GetJsonString());
    }

    public static void SaveModelMetadataToCache(SketchfabModel _model)
    {
        m_Cache.CacheModelMetadata(_model);
    }

    static private async void GltfImport(string _gltfFilePath, Action<GameObject> _onModelImported)
    {
        GltfImport gltf = new GltfImport();

        bool success = true;
        try
        {
            success = await gltf.Load(_gltfFilePath);
        }
        catch (Exception ex)
        {
            success = false;
        }


        if (!success)
        {
            _onModelImported?.Invoke(null);

            return;
        }

        GameObject go = new GameObject("SketchfabModel");
        success = await gltf.InstantiateMainSceneAsync(go.transform);

        if (!success)
        {
            UnityEngine.Object.Destroy(go);
            go = null;
        }

        // else
        // {
        // MeshRenderer[] mrs = go.GetComponentsInChildren<MeshRenderer>();
        // //treat skinned meshrenderer here
        // foreach (var mr in mrs)
        // {
        // Material[] mats = go.GetComponentsInChildren<Material>();
        // foreach (var m in mats)
        // {
        //     //     // Texture2D txt = (Texture2D)m.mainTexture;
        //     //     // mr.material.mainTexture = Resize(txt, 128, 128);
        //     //
        //     //
        //     //     for (int i = 0; i < ShaderUtil.GetPropertyCount(m.shader); i++)
        //     //     {
        //     //         if (ShaderUtil.GetPropertyType(m.shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
        //     //         {
        //     //             Texture2D texture = (Texture2D)m.GetTexture(ShaderUtil.GetPropertyName(m.shader, i));
        //     //             texture = Resize(texture, 128, 128);
        //     //             m.SetTexture(ShaderUtil.GetPropertyName(m.shader, i), texture);
        //     //         }
        //     // }
        //     List<string> textures = new List<string>();
        //     m.GetTexturePropertyNames(textures);
        //     foreach (var t in textures)
        //     {
        //         var temp = Resize((Texture2D)m.GetTexture(t), 128, 128);
        //         m.SetTexture(t, temp);
        //     }
        // }
        // // }
        // // }

        _onModelImported?.Invoke(go);
    }

    static Texture2D Resize(Texture2D texture2D, int targetX, int targetY)
    {
        RenderTexture rt = new RenderTexture(targetX, targetY, 24);
        RenderTexture.active = rt;
        Graphics.Blit(texture2D, rt);
        Texture2D result = new Texture2D(targetX, targetY);
        result.ReadPixels(new Rect(0, 0, targetX, targetY), 0, 0);
        result.Apply();
        return result;
    }


    public static Task<bool> IsUidInCache(string _uid)
    {
        return m_Cache.IsInCache(_uid);
    }

    public static Task<SketchfabModel> GetCachedModelMetadata(string _uid)
    {
        return m_Cache.GetCachedModelMetadata(_uid);
    }
}