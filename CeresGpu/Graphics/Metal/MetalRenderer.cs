using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CeresGLFW;
using CeresGpu.Graphics.Metal.Clearing;
using CeresGpu.Graphics.Shaders;
using Metalancer.MetalBinding;

namespace CeresGpu.Graphics.Metal
{
    public sealed class MetalRenderer : IRenderer
    {
        public readonly IntPtr Context;
        private readonly GLFWWindow _glfwWindow;
        private IntPtr _currentFrameCommandBuffer;
        internal readonly SamplerManager Samplers;

        private MetalPass? _currentPass;

        private ClearRenderer? _clearRenderer;
        
        public int FrameCount => 3; 
        public int WorkingFrame { get; private set; }
        public uint UniqueFrameId { get; private set; }
        public MetalPass? CurrentPass => _currentPass;
        
        public ClearRenderer ClearRenderer {
            get {
                if (_clearRenderer == null) {
                    _clearRenderer = new ClearRenderer(this);
                }
                return _clearRenderer;
            }
        }

        public MetalRenderer(IntPtr window, GLFWWindow glfwWindow)
        {
            _glfwWindow = glfwWindow;
            Context = MetalApi.metalbinding_create(window, (uint)FrameCount);
            MetalApi.metalbinding_arp_drain(Context);
            Samplers = new SamplerManager(this);
        }

        public void Dispose()
        {
            // TODO: More things we have to release? Also shouldn't this have a finalizer too?
            MetalApi.metalbinding_arp_deinit(Context);
            MetalApi.metalbinding_destroy(Context);
        }

        public IBuffer<T> CreateStaticBuffer<T>(int elementCount) where T : unmanaged
        {
            if (elementCount < 0) {
                throw new ArgumentOutOfRangeException(nameof(elementCount));
            }
            
            var buffer = new MetalStaticBuffer<T>(this);
            buffer.Allocate((uint)elementCount);
            return buffer;
        }

        public IBuffer<T> CreateStreamingBuffer<T>(int elementCount) where T : unmanaged
        {
            if (elementCount < 0) {
                throw new ArgumentOutOfRangeException(nameof(elementCount));
            }
            
            var buffer = new MetalStreamingBuffer<T>(this);
            buffer.Allocate((uint)elementCount);
            return buffer;
        }

        public ITexture CreateTexture()
        {
            return new MetalTexture(this);
        }

        public IShaderBacking CreateShaderBacking(IShader shader)
        {
            return new MetalShaderBacking(this, shader);
        }

        public IShaderInstanceBacking CreateShaderInstanceBacking(int vertexBufferCountHint, IShader shader)
        {
            return new MetalShaderInstanceBacking(vertexBufferCountHint);
        }

        public IDescriptorSet CreateDescriptorSet(IShaderBacking shader, ShaderStage stage, int index, in DescriptorSetCreationHints hints)
        {
            if (shader is not MetalShaderBacking metalShader) {
                throw new ArgumentException("Incompatible shader", nameof(shader));
            }

            IntPtr function = stage switch {
                ShaderStage.Vertex => metalShader.VertexFunction
                , ShaderStage.Fragment => metalShader.FragmentFunction
                , _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
            };
            
            return new MetalDescriptorSet(this, function, stage, index, in hints);
        }

        public IPipeline<ShaderT> CreatePipeline<ShaderT>(PipelineDefinition definition, ShaderT shader) where ShaderT : IShader
        {
            return new MetalPipeline<ShaderT>(this, definition, shader);
        }
        
        private void AcquireCurrentFrameCommandBuffer()
        {
            if (_currentFrameCommandBuffer == IntPtr.Zero) {
                
                _glfwWindow.GetContentScale(out float scale, out _);
                _glfwWindow.GetSize(out int width, out int height);
                MetalApi.metalbinding_set_content_scale(Context, scale, (uint)width, (uint)height);
                
                // Acquire this frame's command buffer for the first time.
                _currentFrameCommandBuffer = MetalApi.metalbinding_acquire_command_buffer(Context);
            }
        }

        /// <summary>
        /// To be called by MetalPass when encoding has finished.
        /// </summary>
        public void FinishPass()
        {
            _currentPass = null;
        }

        private MetalPass SetCurrentPass(MetalPass pass)
        {
            _currentPass?.Finish();
            _currentPass = pass;
            return pass;
        }

        public IPass CreateFramebufferPass(bool clear, Vector4 clearColor)
        {
            //MetalApi.metalbinding_capture(Context);
            
            AcquireCurrentFrameCommandBuffer();

            IntPtr passDescriptor = MetalApi.metalbinding_create_current_frame_render_pass_descriptor(Context, clear, clearColor.X, clearColor.Y, clearColor.Z, clearColor.W);
            if (passDescriptor == IntPtr.Zero) {
                throw new InvalidOperationException("Failed to create a pass descriptor for the current frame.");
            }
            try {
                return SetCurrentPass(new MetalPass(this, _currentFrameCommandBuffer, passDescriptor));
            } finally {
                MetalApi.metalbinding_release_render_pass_descriptor(passDescriptor);
            }
        }

        public void Present(float minimumElapsedSeocnds)
        {
            AcquireCurrentFrameCommandBuffer();
            
            _currentPass?.Finish();
            _currentPass = null;
            
            MetalApi.metalbinding_present_current_frame_after_minimum_duration(Context, _currentFrameCommandBuffer, minimumElapsedSeocnds);
            MetalApi.metalbinding_commit_command_buffer(_currentFrameCommandBuffer);
            
            MetalApi.metalbinding_release_command_buffer(_currentFrameCommandBuffer);
            _currentFrameCommandBuffer = IntPtr.Zero;
            
            WorkingFrame = (WorkingFrame + 1) % FrameCount;
            ++UniqueFrameId;

            // New frame actions
            _clearRenderer?.NewFrame();
            
            //MetalApi.metalbinding_stop_capture(Context);

            MetalApi.metalbinding_arp_drain(Context);
        }

        public void GetDiagnosticInfo(IList<(string key, object value)> entries)
        {
            ulong currentAllocatedSize = 0;
            ulong recommendedWorkingSetSize = 0;
            ulong hasUnifiedMemory = 0;
            ulong maxTransferRate = 0;
            
            MetalApi.metalbinding_get_memory_info(Context, ref currentAllocatedSize, ref recommendedWorkingSetSize, ref hasUnifiedMemory, ref maxTransferRate);
            
            entries.Add((nameof(currentAllocatedSize), currentAllocatedSize));
            entries.Add((nameof(recommendedWorkingSetSize), recommendedWorkingSetSize));
            entries.Add((nameof(hasUnifiedMemory), hasUnifiedMemory));
            entries.Add((nameof(maxTransferRate), maxTransferRate));
        }

        // No-BOM utf-8 encoding.
        public static readonly UTF8Encoding UTF8NoBOM = new(false);
        
        public string GetLastError()
        {
            uint len = MetalApi.metalbinding_get_last_error_length(Context);
            IntPtr buffer = Marshal.AllocHGlobal(new IntPtr(len));
            try {
                MetalApi.metalbinding_get_last_error(Context, buffer, len);
                unsafe {
                    return UTF8NoBOM.GetString((byte*)buffer, checked((int)len));
                }
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}