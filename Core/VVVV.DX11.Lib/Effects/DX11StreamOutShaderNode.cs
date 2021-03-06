﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.ComponentModel.Composition;

using SlimDX;
using SlimDX.DXGI;
using SlimDX.Direct3D11;
using Device = SlimDX.Direct3D11.Device;
using Buffer = SlimDX.Direct3D11.Buffer;


using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.DX11.Internals;

using VVVV.DX11.Lib.Effects;
using FeralTic.DX11;
using FeralTic.DX11.Resources;
using FeralTic.DX11.Utils;


namespace VVVV.DX11.Nodes.Layers
{
    [PluginInfo(Name = "ShaderNode", Category = "DX11", Version = "", Author = "vux")]
    public unsafe class DX11StreamOutShaderNode : DX11BaseShaderNode, IPluginBase, IPluginEvaluate, IDisposable, IDX11ResourceHost
    {
        public enum GeometryBuildMode
        {
            Single,
            Combine
        }

        private int tid = 0;

        private DX11ObjectRenderSettings objectsettings = new DX11ObjectRenderSettings();

        private DX11ShaderVariableManager varmanager;
        private DX11ContextElement<DX11ShaderData> deviceshaderdata = new DX11ContextElement<DX11ShaderData>();
        private DX11ContextElement<DX11ShaderVariableCache> shaderVariableCache = new DX11ContextElement<DX11ShaderVariableCache>();

        private DX11RenderSettings settings = new DX11RenderSettings();

        private int spmax = 0;
        private int layoutsize;
        private InputElement[] elems;

        private IDX11Geometry clone;
        private Buffer buffer;

        private DX11RenderSettings renderSettings = new DX11RenderSettings();
        private DX11ObjectRenderSettings objectSettings = new DX11ObjectRenderSettings();

        #region Default Input Pins
        [Input("Geometry In", CheckIfChanged=true)]
        protected Pin<DX11Resource<IDX11Geometry>> FIn;

        [Input("Transform In")]
        protected ISpread<Matrix> FInWorld;

        [Input("View",Order = 10001)]
        protected ISpread<Matrix> FInView;

        [Input("Projection", Order = 10002, IsSingle = true)]
        protected ISpread<Matrix> FInProjection;

        [Input("As Auto", Order = 10003, IsSingle = true)]
        protected IDiffSpread<bool> FInAsAuto;

        [Input("Auto Layout", Order = 10005, CheckIfChanged = true)]
        protected IDiffSpread<bool> FInAutoLayout;

        [Input("Max Elements", Order = 10004, IsSingle = true)]
        protected IDiffSpread<int> FInMaxElements;

        [Input("Output Layout", Order = 10005, CheckIfChanged = true)]
        protected Pin<InputElement> FInLayout;

        [Input("Apply Mode", Order = 10006, IsSingle = true, Visibility = PinVisibility.OnlyInspector)]
        protected IDiffSpread<GeometryBuildMode> FBuildMode;

        [Input("Custom Semantics", Order = 50000, Visibility = PinVisibility.OnlyInspector)]
        protected Pin<IDX11RenderSemantic> FInSemantics;

        [Input("Resource Semantics", Order = 50001, Visibility = PinVisibility.OnlyInspector)]
        protected Pin<DX11Resource<IDX11RenderSemantic>> FInResSemantics;
        #endregion

        #region Output Pins
        [Output("Geometry Out")]
        protected ISpread<DX11Resource<IDX11Geometry>> FOut;

        [Output("Buffer Out")]
        protected ISpread<DX11Resource<DX11RawBuffer>> FOutBuffer;

        [Output("Technique Valid")]
        protected ISpread<bool> FOutTechniqueValid;
        #endregion

        #region Set the shader instance
        public override void SetShader(DX11Effect shader, bool isnew, string fileName)
        {
            FOutPath.SliceCount = 1;
            FOutPath[0] = fileName;
            
            if (isnew) { this.FShader = shader; }

            if (shader.IsCompiled)
            {
                this.FShader = shader;
                this.varmanager.SetShader(shader);
                this.shaderVariableCache.Clear();
                this.deviceshaderdata.Dispose();
            }

            //Only set technique if new, otherwise do it on update/evaluate
            if (isnew)
            {
                string defaultenum;
                if (shader.IsCompiled)
                {
                    defaultenum = shader.TechniqueNames[0];
                    this.FHost.UpdateEnum(this.TechniqueEnumId, shader.TechniqueNames[0], shader.TechniqueNames);
                    this.varmanager.CreateShaderPins();
                }
                else
                {
                    defaultenum = "";
                    this.FHost.UpdateEnum(this.TechniqueEnumId, "", new string[0]);
                }
            }
            else
            {
                if (shader.IsCompiled)
                {
                    this.FHost.UpdateEnum(this.TechniqueEnumId, shader.TechniqueNames[0], shader.TechniqueNames);
                    this.varmanager.UpdateShaderPins();
                }
            }
            this.FInvalidate = true;
        }
        #endregion

        #region Constructor
        [ImportingConstructor()]
        public DX11StreamOutShaderNode(IPluginHost host, IIOFactory factory)
        {
            this.FHost = host;
            this.FFactory = factory;
            this.TechniqueEnumId = Guid.NewGuid().ToString();

            InputAttribute inAttr = new InputAttribute("Technique");
            inAttr.EnumName = this.TechniqueEnumId;
            //inAttr.DefaultEnumEntry = defaultenum;
            inAttr.Order = 1000;
            this.FInTechnique = this.FFactory.CreateDiffSpread<EnumEntry>(inAttr);

            this.varmanager = new DX11ImageShaderVariableManager(host, factory);


        }
        #endregion

        #region Evaluate
        public void Evaluate(int SpreadMax)
        {
            this.spmax = this.CalculateSpreadMax();

            if (this.spmax == 0)
            {
                if (this.FOut.SliceCount == 0) // Already 0
                    return;

                if (this.FOut[0] != null)
                {
                    this.FOut[0].Dispose();
                }
                if (this.FOutBuffer[0] != null)
                {
                    this.FOutBuffer[0].Dispose();
                }
                this.FOut.SliceCount = 0;
                this.FOutBuffer.SliceCount = 0;
                return;
            }
            else
            {
                this.FOutBuffer.SliceCount = 1;
                this.FOut.SliceCount = 1;
            }

            if (this.FOut[0] == null)
            {
                this.FOut[0] = new DX11Resource<IDX11Geometry>();
                this.FOutBuffer[0] = new DX11Resource<DX11RawBuffer>();
            }
            
            if (this.FInvalidate)
            {
                if (this.FShader.IsCompiled)
                {
                    this.FOutCompiled[0] = true;
                    this.FOutTechniqueValid.SliceCount = this.FShader.TechniqueValids.Length;

                    for (int i = 0; i < this.FShader.TechniqueValids.Length; i++)
                    {
                        this.FOutTechniqueValid[i] = this.FShader.TechniqueValids[i];
                    }
                }
                else
                {
                    this.FOutCompiled[0] = false;
                    this.FOutTechniqueValid.SliceCount = 0;
                }
                this.FInvalidate = false;
            }

            if (this.FInTechnique.IsChanged)
            {
                tid = this.FInTechnique[0].Index;
                this.techniquechanged = true;
            }
            this.FOut.Stream.IsChanged = true;
            this.FOutBuffer.Stream.IsChanged = true;

            this.varmanager.ApplyUpdates();
        }

        #endregion

        #region Calculate Spread Max
        private int CalculateSpreadMax()
        {
            if (this.FIn.SliceCount == 0 || this.FInView.SliceCount == 0 || this.FInProjection.SliceCount == 0 || this.FInWorld.SliceCount == 0)
                return 0;

            int max = this.varmanager.CalculateSpreadMax();

            if (max == 0 || this.FIn.SliceCount == 0)
            {
                return 0;
            }
            else
            {
                int mvp = Math.Max(this.FInView.SliceCount, this.FInProjection.SliceCount);
                max = Math.Max(this.FIn.SliceCount, max);
                max = Math.Max(max, mvp);
                max = Math.Max(max, this.FInWorld.SliceCount);
                return max;
            }
        }
        #endregion

        #region Update
        public void Update(DX11RenderContext context)
        {
            int spreadMax = this.CalculateSpreadMax();

            if (spreadMax == 0)
            {
                return;
            }

            Device device = context.Device;
            DeviceContext ctx = context.CurrentDeviceContext;

            if (!this.deviceshaderdata.Contains(context))
            {
                this.deviceshaderdata[context]  = new DX11ShaderData(context, this.FShader);
            }
            if (!this.shaderVariableCache.Contains(context))
            {
                this.shaderVariableCache[context] = new DX11ShaderVariableCache(context, this.deviceshaderdata[context].ShaderInstance, this.varmanager);
            }

            DX11ShaderData shaderdata = this.deviceshaderdata[context];
            shaderdata.Update(this.FInTechnique[0].Index, 0, this.FIn);
            
            bool customlayout = this.FInLayout.IsConnected || this.FInAutoLayout[0];
            if (this.techniquechanged || this.FInLayout.IsChanged || this.FInAutoLayout.IsChanged)
            {
                elems = null;
                int size = 0;

                if (this.FInAutoLayout[0])
                {
                    elems = this.FShader.DefaultEffect.GetTechniqueByIndex(tid).GetPassByIndex(0).GetStreamOutputLayout(out size);
                }
                else
                {
                    if (customlayout)
                    {
                        elems = this.BindInputLayout(out size);
                    }
                }
                this.layoutsize = size;
            }

            if (this.FInEnabled[0] && this.FIn.IsConnected)
            {
                //Clear shader stages
                shaderdata.ResetShaderStages(ctx);

                this.InitRenderSettings(context);


                if (this.FIn.IsChanged || this.techniquechanged || shaderdata.LayoutValid.Count == 0)
                {
                    shaderdata.Update(this.FInTechnique[0].Index, 0, this.FIn);
                    this.techniquechanged = false;
                }


                if (shaderdata.IsLayoutValid(0) && this.varmanager.SetGlobalSettings(shaderdata.ShaderInstance,this.settings))
                {
                    this.OnBeginQuery(context);


                    if (this.clone == null || this.FIn.IsChanged || this.FInAsAuto.IsChanged || this.FInMaxElements.IsChanged || this.FInLayout.IsChanged || this.FInAutoLayout.IsChanged)
                    {
                        this.CreateOutputBuffer(context, customlayout);

                    }

                    var variableCache = this.shaderVariableCache[context];


                    int writeCount = 1;
                    if (this.FBuildMode[0] == GeometryBuildMode.Combine)
                    {
                        writeCount = spreadMax;
                    }

                    for (int i = 0; i < writeCount; i++)
                    {
                        if (shaderdata.IsLayoutValid(i))
                        {
                            this.settings.ApplyTransforms(this.FInView[i], this.FInProjection[i], Matrix.Identity, Matrix.Identity);

                            variableCache.ApplyGlobals(settings);

                            ctx.StreamOutput.SetTargets(new StreamOutputBufferBinding(this.buffer, i == 0 ? 0 : -1));
                            shaderdata.SetInputAssembler(ctx, this.FIn[i][context], 0);

                            this.objectsettings.DrawCallIndex = 0;
                            this.objectsettings.Geometry = this.FIn[i][context];
                            this.objectsettings.WorldTransform = this.FInWorld[i];

                            variableCache.ApplySlice(this.objectsettings, i);

                            shaderdata.ApplyPass(ctx);

                            this.FIn[i][context].Draw();
                        }
                    }

                    ctx.StreamOutput.SetTargets(null);

                    this.FOut[0][context] = this.clone;

                    this.OnEndQuery(context);

                    
                }
                else
                {
                    this.FOut[0][context] = this.FIn[0][context];
                }
            }
            else
            {
                this.FOut[0][context] = this.FIn[0][context];
            }
            
        }

        #endregion

        #region Initialize render settings
        private void InitRenderSettings(DX11RenderContext context)
        {
            this.settings.RenderWidth = 1;
            this.settings.RenderHeight = 1;
            this.settings.RenderDepth = 1;
            
            //Clear from old frame if applicable
            this.settings.CustomSemantics.Clear();
            this.settings.ResourceSemantics.Clear();
            this.settings.BackBuffer = null;

            for (int i = 0; i < this.FInSemantics.SliceCount; i++)
            {
                if (this.FInSemantics[i] != null)
                {
                    this.settings.CustomSemantics.Add(this.FInSemantics[i]);
                }
            }

            for (int i = 0; i < this.FInResSemantics.SliceCount; i++)
            {
                if (this.FInResSemantics[i] != null && this.FInResSemantics[i].Contains(context))
                {
                    this.settings.ResourceSemantics.Add(this.FInResSemantics[i]);
                }
            }
        }
        #endregion

        private void CreateOutputBuffer(DX11RenderContext context, bool customlayout)
        {
            if (this.buffer != null) { this.buffer.Dispose(); }

            #region Vertex Geom
            if (this.FIn[0][context] is DX11VertexGeometry)
            {
                if (!this.FInAsAuto[0])
                {
                    DX11VertexGeometry vg = (DX11VertexGeometry)this.FIn[0][context].ShallowCopy();

                    int vsize = customlayout ? this.layoutsize : vg.VertexSize;
                    Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, vsize, vg.VerticesCount);
                    if (customlayout) { vg.VertexSize = vsize; }
                    vg.VertexBuffer = vbo;

                    this.clone = vg;
                    this.buffer = vbo;
                }
                else
                {
                    DX11VertexGeometry vg = (DX11VertexGeometry)this.FIn[0][context].ShallowCopy();

                    int maxv = vg.VerticesCount;
                    if (this.FInMaxElements[0] > 0)
                    {
                        maxv = this.FInMaxElements[0];
                    }

                    int vsize = customlayout ? this.layoutsize : vg.VertexSize;
                    Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, vsize, maxv);
                    vg.VertexBuffer = vbo;
                    vg.AssignDrawer(new DX11VertexAutoDrawer());
                    if (customlayout) { vg.VertexSize = vsize; }

                    this.clone = vg;
                    this.buffer = vbo;
                }
            }
            #endregion

            #region Inxexed geom
            if (this.FIn[0][context] is DX11IndexedGeometry)
            {
                if (!this.FInAsAuto[0])
                {
                    DX11IndexedGeometry inputIndexedGeometry = (DX11IndexedGeometry)this.FIn[0][context].ShallowCopy();

                    int vertexSize = customlayout ? this.layoutsize : inputIndexedGeometry.VertexSize;

                    Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, vertexSize, inputIndexedGeometry.VerticesCount);
                    inputIndexedGeometry.VertexBuffer = vbo;

                    if (customlayout)
                    {
                        inputIndexedGeometry.VertexSize = vertexSize;
                    }
                    this.clone = inputIndexedGeometry;
                    this.buffer = vbo;
                }
                else
                {
                    //Need to rebind indexed geom as vertex
                    DX11IndexedGeometry inputIndexedGeometry = (DX11IndexedGeometry)this.FIn[0][context];

                    int maxVertexCount = inputIndexedGeometry.VerticesCount;
                    if (this.FInMaxElements[0] > 0)
                    {
                        maxVertexCount = this.FInMaxElements[0];
                    }

                    int vertexSize = customlayout ? this.layoutsize : inputIndexedGeometry.VertexSize;
                    DX11VertexGeometry outputVertexGeometry = DX11VertexGeometry.StreamOut(context, maxVertexCount, vertexSize, true);
                    outputVertexGeometry.BoundingBox = inputIndexedGeometry.BoundingBox;
                    outputVertexGeometry.HasBoundingBox = inputIndexedGeometry.HasBoundingBox;
                    outputVertexGeometry.InputLayout = inputIndexedGeometry.InputLayout;
                    outputVertexGeometry.Topology = inputIndexedGeometry.Topology;
                    this.clone = outputVertexGeometry;
                    this.buffer = outputVertexGeometry.VertexBuffer;
                }
            }
            #endregion

            #region Null geom
            if (this.FIn[0][context] is DX11NullGeometry)
            {
                DX11NullGeometry inputNullGeometry = (DX11NullGeometry)this.FIn[0][context];

                DX11VertexGeometry outputVertexGeometry = DX11VertexGeometry.StreamOut(context, this.FInMaxElements[0], this.layoutsize, true);
                outputVertexGeometry.BoundingBox = inputNullGeometry.BoundingBox;
                outputVertexGeometry.HasBoundingBox = inputNullGeometry.HasBoundingBox;
                outputVertexGeometry.InputLayout = inputNullGeometry.InputLayout;
                outputVertexGeometry.Topology = inputNullGeometry.Topology;
                this.clone = outputVertexGeometry;
                this.buffer = outputVertexGeometry.VertexBuffer;

            }
            #endregion

            #region Index Only geom
            if (this.FIn[0][context] is DX11IndexOnlyGeometry)
            {
                DX11IndexOnlyGeometry ng = (DX11IndexOnlyGeometry)this.FIn[0][context];

                Buffer vbo = BufferHelper.CreateStreamOutBuffer(context, this.layoutsize, this.FInMaxElements[0]);

                //Copy a new Vertex buffer with stream out
                DX11VertexGeometry vg = new DX11VertexGeometry(context);
                vg.AssignDrawer(new DX11VertexAutoDrawer());
                vg.BoundingBox = ng.BoundingBox;
                vg.HasBoundingBox = ng.HasBoundingBox;
                vg.InputLayout = ng.InputLayout;
                vg.Topology = ng.Topology;
                vg.VertexBuffer = vbo;
                vg.VertexSize = this.layoutsize;
                vg.VerticesCount = this.FInMaxElements[0];

                this.clone = vg;
                this.buffer = vbo;

            }
            #endregion

            if (customlayout) { this.clone.InputLayout = elems; }

            if (this.FOutBuffer[0][context] != null)
            {
                this.FOutBuffer[0][context].SRV.Dispose();
            }

            if (context.ComputeShaderSupport)
            {
                this.FOutBuffer[0][context] = new DX11RawBuffer(context, this.buffer);
            }
            else
            {
                this.FOutBuffer[0][context] = null;
            }
        }

        #region Destroy
        public void Destroy(DX11RenderContext context, bool force)
        {
            if (force)
            {
                this.deviceshaderdata.Dispose(context);
                this.shaderVariableCache.Dispose(context);
            }
        }
        #endregion

        #region Dispose
        public void Dispose()
        {
            this.deviceshaderdata.Dispose();
            this.shaderVariableCache.Dispose();
            if (this.buffer != null)
            {
                this.buffer.Dispose();
                this.buffer = null;

            }
        }
        #endregion

        private InputElement[] BindInputLayout(out int vertexsize)
        {
            InputElement[] inputlayout = new InputElement[this.FInLayout.SliceCount];
            vertexsize = 0;
            for (int i = 0; i < this.FInLayout.SliceCount; i++)
            {

                if (this.FInLayout.IsConnected && this.FInLayout[i] != null)
                {
                    inputlayout[i] = this.FInLayout[i];
                }
                else
                {
                    //Set default, can do better here
                    inputlayout[i] = new InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0);
                }
                vertexsize += FormatHelper.Instance.GetSize(inputlayout[i].Format);
            }
            InputLayoutFactory.AutoIndex(inputlayout);

            return inputlayout;
        }

    }
}
