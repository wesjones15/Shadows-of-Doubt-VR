using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SoDVR.VR;

/// <summary>
/// Canvases of the mod's own that take over part of a game canvas: laid out exactly like it, so
/// game UI moved across keeps its place and the game keeps its references to it.
/// </summary>
internal static class ModCanvas
{
    public static Canvas CreateLike(Canvas gameCanvas, string name)
    {
        var go = new GameObject(name);
        go.layer = gameCanvas.gameObject.layer;
        // Same lifetime as the game canvas, so a scene change never leaves the game holding
        // destroyed UI, nor destroys ours from under it.
        if (gameCanvas.gameObject.scene.name == "DontDestroyOnLoad") Object.DontDestroyOnLoad(go);
        else SceneManager.MoveGameObjectToScene(go, gameCanvas.gameObject.scene);

        var canvas = go.AddComponent<Canvas>();
        CopyScaler(gameCanvas, go.AddComponent<CanvasScaler>());
        return canvas;
    }

    private static void CopyScaler(Canvas from, CanvasScaler to)
    {
        var source = from.GetComponent<CanvasScaler>();
        if (source == null) return;
        to.uiScaleMode = source.uiScaleMode;
        to.referenceResolution = source.referenceResolution;
        to.screenMatchMode = source.screenMatchMode;
        to.matchWidthOrHeight = source.matchWidthOrHeight;
        to.scaleFactor = source.scaleFactor;
        to.referencePixelsPerUnit = source.referencePixelsPerUnit;
    }
}
