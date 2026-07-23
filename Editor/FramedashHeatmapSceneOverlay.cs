using System;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine.UIElements;

namespace Framedash.Editor
{
    [Overlay(typeof(SceneView), "Framedash Heatmap", defaultDisplay = true)]
    public sealed class FramedashHeatmapSceneOverlay : Overlay
    {
        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement();
            root.style.flexDirection = FlexDirection.Row;

            var showToggle = new Toggle("Show")
            {
                value = FramedashHeatmapOverlayService.IsEnabled
            };
            showToggle.RegisterValueChangedCallback(
                change => FramedashHeatmapOverlayService.SetEnabled(change.newValue));
            root.Add(showToggle);

            var frameButton = new Button(
                () => FramedashHeatmapOverlayService.FrameHeatmap(containerWindow as SceneView))
            {
                text = "Frame"
            };
            root.Add(frameButton);

            var controlsButton = new Button(FramedashHeatmapWindow.ShowWindow)
            {
                text = "Controls"
            };
            root.Add(controlsButton);

            Action synchronize = () =>
            {
                showToggle.SetValueWithoutNotify(FramedashHeatmapOverlayService.IsEnabled);
                frameButton.SetEnabled(FramedashHeatmapOverlayService.HasData);
            };
            bool subscribed = false;
            Action subscribe = () =>
            {
                if (subscribed)
                {
                    return;
                }
                FramedashHeatmapOverlayService.StateChanged += synchronize;
                subscribed = true;
                synchronize();
            };
            Action unsubscribe = () =>
            {
                if (!subscribed)
                {
                    return;
                }
                FramedashHeatmapOverlayService.StateChanged -= synchronize;
                subscribed = false;
            };
            subscribe();
            root.RegisterCallback<AttachToPanelEvent>(_ => subscribe());
            root.RegisterCallback<DetachFromPanelEvent>(
                _ => unsubscribe());
            return root;
        }
    }
}
