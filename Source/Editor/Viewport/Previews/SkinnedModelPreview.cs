using System;
using FlaxEditor.GUI.ContextMenu;
using FlaxEngine;
using FlaxEngine.GUI;
using FlaxEditor.Viewport.Widgets;

namespace FlaxEditor.Viewport.Previews
{
    /// <summary>
    /// Animation asset preview editor viewport.
    /// </summary>
    /// <seealso cref="AnimatedModelPreview" />
    public class SkinnedModelPreview : AnimatedModelPreview
    {
        private bool _showCurrentLOD;
        private ContextMenuButton _showCurrentLODButton;

        /// <summary>
        /// The "PreviewLODS" widget button context menu.
        /// </summary>
        private ContextMenu previewLODSWidgetButtonMenu;

        /// <summary>
        /// Gets or sets a value that shows LOD statistics
        /// </summary>
        public bool ShowCurrentLOD
        {
            get => _showCurrentLOD;
            set
            {
                if (_showCurrentLOD == value)
                    return;
                _showCurrentLOD = value;
                if (value)
                    ShowDebugDraw = true;
                if (_showCurrentLODButton != null)
                    _showCurrentLODButton.Checked = value;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SkinnedModelPreview"/> class.
        /// </summary>
        /// <param name="useWidgets">if set to <c>true</c> use widgets.</param>
        public SkinnedModelPreview(bool useWidgets)
        : base(useWidgets)
        {
            if (useWidgets)
            {
                // Show Current LOD
                _showCurrentLODButton = ViewWidgetShowMenu.AddButton("Current LOD", button =>
                {
                    _showCurrentLOD = !_showCurrentLOD;
                    _showCurrentLODButton.Icon = _showCurrentLOD ? Style.Current.CheckBoxTick : SpriteHandle.Invalid;
                });
                _showCurrentLODButton.IndexInParent = 2;

                // PreviewLODS mode widget
                var PreviewLODSMode = new ViewportWidgetsContainer(ViewportWidgetLocation.UpperRight);
                previewLODSWidgetButtonMenu = new ContextMenu();
                previewLODSWidgetButtonMenu.VisibleChanged += PreviewLODSWidgetMenuOnVisibleChanged;
                var previewLODSModeButton = new ViewportWidgetButton("Preview LOD", SpriteHandle.Invalid, previewLODSWidgetButtonMenu)
                {
                    TooltipText = "Preview LOD properties",
                    Parent = PreviewLODSMode,
                };
                PreviewLODSMode.Parent = this;
            }
        }

        /// <summary>
        /// Fill out all SkinnedModel LODS
        /// </summary>
        /// <param name="control"></param>
        private void PreviewLODSWidgetMenuOnVisibleChanged(Control control)
        {
            if (!control.Visible)
                return;

            var skinned = _previewModel.SkinnedModel;
            if (skinned && !skinned.WaitForLoaded() && skinned.IsLoaded)
            {
                previewLODSWidgetButtonMenu.ItemsContainer.DisposeChildren();
                var lods = skinned.LODs.Length;
                for (int i = -1; i < lods; i++)
                {
                    var index = i;
                    var button = previewLODSWidgetButtonMenu.AddButton("LOD " + (index == -1 ? "Auto" : index));
                    button.ButtonClicked += (button) => _previewModel.ForcedLOD = index;
                    button.Checked = _previewModel.ForcedLOD == index;
                    button.Tag = index;
                    if (lods <= 1) return;
                }
            }
        }

        /// <inheritdoc />
        private int ComputeLODIndex(SkinnedModel model, out float screenSize)
        {
            screenSize = 1.0f;
            if (PreviewActor.ForcedLOD != -1)
                return PreviewActor.ForcedLOD;

            // Based on RenderTools::ComputeModelLOD
            CreateProjectionMatrix(out var projectionMatrix);
            float screenMultiple = 0.5f * Mathf.Max(projectionMatrix.M11, projectionMatrix.M22);
            var sphere = PreviewActor.Sphere;
            var viewOrigin = ViewPosition;
            var distSqr = Vector3.DistanceSquared(ref sphere.Center, ref viewOrigin);
            var screenRadiusSquared = Mathf.Square(screenMultiple * sphere.Radius) / Mathf.Max(1.0f, distSqr);
            screenSize = Mathf.Sqrt((float)screenRadiusSquared) * 2.0f;

            // Check if model is being culled
            if (Mathf.Square(model.MinScreenSize * 0.5f) > screenRadiusSquared)
                return -1;

            // Skip if no need to calculate LOD
            if (model.LoadedLODs == 0)
                return -1;
            var lods = model.LODs;
            if (lods.Length == 0)
                return -1;
            if (lods.Length == 1)
                return 0;

            // Iterate backwards and return the first matching LOD
            for (int lodIndex = lods.Length - 1; lodIndex >= 0; lodIndex--)
            {
                if (Mathf.Square(lods[lodIndex].ScreenSize * 0.5f) >= screenRadiusSquared)
                {
                    return lodIndex + PreviewActor.LODBias;
                }
            }

            return 0;
        }

        /// <inheritdoc />
        public override void Draw()
        {
            base.Draw();

            var skinnedModel = _previewModel.SkinnedModel;
            if (skinnedModel == null || !skinnedModel.IsLoaded)
                return;
            var lods = skinnedModel.LODs;
            if (lods.Length == 0)
            {
                // Force show skeleton for models without geometry
                ShowNodes = true;
                return;
            }
            if (_showCurrentLOD)
            {
                var lodIndex = ComputeLODIndex(skinnedModel, out var screenSize);
                var auto = _previewModel.ForcedLOD == -1;
                string text = auto ? "LOD Automatic" : "";
                text += auto ? string.Format("\nScreen Size: {0:F2}", screenSize) : "";
                text += string.Format("\nCurrent LOD: {0}", lodIndex);
                if (lodIndex != -1)
                {
                    lodIndex = Mathf.Clamp(lodIndex + PreviewActor.LODBias, 0, lods.Length - 1);
                    var lod = lods[lodIndex];
                    int triangleCount = 0, vertexCount = 0;
                    for (int meshIndex = 0; meshIndex < lod.Meshes.Length; meshIndex++)
                    {
                        var mesh = lod.Meshes[meshIndex];
                        triangleCount += mesh.TriangleCount;
                        vertexCount += mesh.VertexCount;
                    }
                    text += string.Format("\nTriangles: {0:N0}\nVertices: {1:N0}", triangleCount, vertexCount);
                }
                var font = Style.Current.FontMedium;
                var pos = new Float2(10, 50);
                Render2D.DrawText(font, text, new Rectangle(pos + Float2.One, Size), Color.Black);
                Render2D.DrawText(font, text, new Rectangle(pos, Size), Color.White);
            }
        }

        /// <inheritdoc />
        public override void OnDestroy()
        {
            _showCurrentLODButton = null;

            base.OnDestroy();
        }
    }
}