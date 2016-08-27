using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12HelloCompute
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.Diagnostics;

    internal class D3D12HelloCompute : IDisposable
    {
        private struct ComputeInputStruct
        {
            public int number;
        };

        private struct ComputeOutputStruct
        {
            public int result;
        };

        private const int FrameCount = 2;
        private const int ThreadBlockSize = 128;
        private const int ComputeProcessCount = ThreadBlockSize;

        private Device Device;
        private CommandQueue CommandQueue;
        private CommandQueue ComputeCommandQueue;
        private SwapChain3 SwapChain;
        private RootSignature ComputeRootSignature;
        private PipelineState ComputePipelineState;
        private int FrameIndex;
        private DescriptorHeap RenderTargetViewHeap;
        private int RtvDescriptorSize;
        private Resource[] RenderTargets = new Resource[FrameCount];
        private CommandAllocator CommandAllocator;
        private CommandAllocator ComputeCommandAllocator;
        private GraphicsCommandList CommandList;
        private GraphicsCommandList ComputeCommandList;
        private int FenceValue;
        private Fence Fence;
        private Fence ComputeFence;
        private AutoResetEvent FenceEvent;
        private DescriptorHeap CbvSrvUavHeap;
        private int CbvSrvUavDescriptorSize;
        private int InputBufferSize;
        private Resource ComputeInputBuffer;
        private Resource ComputeInputBufferUpload;
        private IntPtr ComputeInputBufferUploadPtr;
        private int OutputBufferSize;
        private Resource ComputeOutputBuffer;
        private Resource ComputeOutputBufferUpload;
        private IntPtr ComputeOutputBufferUploadPtr;
        private ComputeInputStruct[] ComputeInputData = new ComputeInputStruct[ComputeProcessCount];
        private ComputeOutputStruct[] ComputeOutputData = new ComputeOutputStruct[ComputeProcessCount];

        public void Dispose()
        {
            WaitForPreviousFrame();

            foreach (var target in RenderTargets)
            {
                target.Dispose();
            }

            ComputeRootSignature.Dispose();
            ComputePipelineState.Dispose();
            ComputeInputBuffer.Dispose();
            ComputeInputBufferUpload.Dispose();
            ComputeOutputBuffer.Dispose();
            ComputeOutputBufferUpload.Dispose();
            CommandAllocator.Dispose();
            ComputeCommandAllocator.Dispose();
            CommandQueue.Dispose();
            ComputeCommandQueue.Dispose();
            RenderTargetViewHeap.Dispose();
            CbvSrvUavHeap.Dispose();
            CommandList.Dispose();
            ComputeCommandList.Dispose();
            Fence.Dispose();
            ComputeFence.Dispose();
            SwapChain.Dispose();
            Device.Dispose();
        }

        internal void Initialize(RenderForm form)
        {
            LoadPipeline(form);
            LoadAssets();
        }

        private void LoadPipeline(RenderForm form)
        {
            var width = form.ClientSize.Width;
            var height = form.ClientSize.Height;

#if DEBUG
            {
                DebugInterface.Get().EnableDebugLayer();
            }
#endif

            Device = new Device(null, SharpDX.Direct3D.FeatureLevel.Level_11_0);

            using (var factory = new Factory4())
            {
                var commandQueueDesc = new CommandQueueDescription(CommandListType.Direct);
                CommandQueue = Device.CreateCommandQueue(commandQueueDesc);

                // コンピュート用のキューを作成
                var computeQueueDesc = new CommandQueueDescription(CommandListType.Compute);
                ComputeCommandQueue = Device.CreateCommandQueue(computeQueueDesc);

                var swapChainDesc = new SwapChainDescription()
                {
                    BufferCount = FrameCount,
                    ModeDescription = new ModeDescription(width, height, new Rational(60, 1), Format.R8G8B8A8_UNorm),
                    Usage = Usage.RenderTargetOutput,
                    SwapEffect = SwapEffect.FlipDiscard,
                    OutputHandle = form.Handle,
                    Flags = SwapChainFlags.None,
                    SampleDescription = new SampleDescription(1, 0),
                    IsWindowed = true,
                };

                using (var tempSwapChain = new SwapChain(factory, CommandQueue, swapChainDesc))
                {
                    SwapChain = tempSwapChain.QueryInterface<SwapChain3>();
                    FrameIndex = SwapChain.CurrentBackBufferIndex;
                }

                factory.MakeWindowAssociation(form.Handle, WindowAssociationFlags.IgnoreAltEnter);
            }

            var rtvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = FrameCount,
                Type = DescriptorHeapType.RenderTargetView,
                Flags = DescriptorHeapFlags.None,
            };
            RenderTargetViewHeap = Device.CreateDescriptorHeap(rtvHeapDesc);
            RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            for (var i = 0; i < FrameCount; i++)
            {
                RenderTargets[i] = SwapChain.GetBackBuffer<Resource>(i);
                Device.CreateRenderTargetView(RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += RtvDescriptorSize;
            }

            // CBV/SRV/UAV 用のデスクリプタヒープを作成
            var cbvSrvUavHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = 2,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible,
            };
            CbvSrvUavHeap = Device.CreateDescriptorHeap(cbvSrvUavHeapDesc);
            CbvSrvUavDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            CommandAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
            // コンピュート用のコマンドアロケータを作成
            ComputeCommandAllocator = Device.CreateCommandAllocator(CommandListType.Compute);
        }

        private void LoadAssets()
        {
            // ルートシグネチャの作成
            {
                // コンピュート用のルートシグネチャを作成
                var computeRootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.None,
                    new[]
                    {
                    new RootParameter(ShaderVisibility.All,
                        // 入力バッファとなる SRV
                        new DescriptorRange()
                        {
                            RangeType = DescriptorRangeType.ShaderResourceView,
                            BaseShaderRegister = 0,
                            OffsetInDescriptorsFromTableStart = 0,
                            DescriptorCount = 1,
                        },
                        // 出力バッファとなる UAV
                        new DescriptorRange()
                        {
                            RangeType = DescriptorRangeType.UnorderedAccessView,
                            BaseShaderRegister = 0,
                            OffsetInDescriptorsFromTableStart = 1,
                            DescriptorCount = 1,
                        }),
                    });
                ComputeRootSignature = Device.CreateRootSignature(computeRootSignatureDesc.Serialize());
            }

            // パイプラインステートオブジェクトの作成
            {
                // シェーダをロードします。デバッグビルドのときは、デバッグフラグを立てます。
#if DEBUG
                var computeShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Compute.hlsl", "CSMain", "cs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
#else
                var computeShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Compute.hlsl", "CSMain", "cs_5_0"));
#endif

                // コンピュート用のパイプラインステートオブジェクトの作成
                var computePsoDesc = new ComputePipelineStateDescription()
                {
                    RootSignature = ComputeRootSignature,
                    ComputeShader = computeShader,
                };
                ComputePipelineState = Device.CreateComputePipelineState(computePsoDesc);
            }

            // コマンドリストの作成
            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocator, null);
            ComputeCommandList = Device.CreateCommandList(CommandListType.Compute, ComputeCommandAllocator, ComputePipelineState);
            CommandList.Close();
            ComputeCommandList.Close();

            // コンピュートシェーダに渡す入力バッファと出力バッファを作成します。
            {
                InputBufferSize = ComputeProcessCount * Utilities.SizeOf<ComputeInputStruct>();

                // コンピュートシェーダで処理する入力バッファを作成します。
                ComputeInputBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Buffer(InputBufferSize),
                    ResourceStates.NonPixelShaderResource
                    );

                // 入力バッファを更新するためのアップロードバッファを作成します。
                ComputeInputBufferUpload = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(InputBufferSize),
                    ResourceStates.GenericRead
                    );

                // アップロード用のデータを初期化します。
                for (var i = 0; i < ComputeProcessCount; i++)
                {
                    ComputeInputData[i].number = i;
                }

                // アップロード用のバッファを初期化します。
                ComputeInputBufferUploadPtr = ComputeInputBufferUpload.Map(0);
                var currentComputeInputBufferUploadPtr = ComputeInputBufferUploadPtr;
                for (var i = 0; i < ComputeProcessCount; i++)
                {
                    currentComputeInputBufferUploadPtr = Utilities.WriteAndPosition(currentComputeInputBufferUploadPtr, ref ComputeInputData[i]);
                }

                OutputBufferSize = ComputeProcessCount * Utilities.SizeOf<ComputeOutputStruct>();

                // コンピュートシェーダで処理する出力バッファを作成します。
                ComputeOutputBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Buffer(OutputBufferSize, ResourceFlags.AllowUnorderedAccess),
                    ResourceStates.GenericRead
                    );

                // コンピュートシェーダの出力バッファを直接マップすることはできないため、
                // 出力バッファの内容を写しとるためのバッファを作成します。
                ComputeOutputBufferUpload = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(OutputBufferSize),
                    ResourceStates.GenericRead
                    );
                ComputeOutputBufferUploadPtr = ComputeOutputBufferUpload.Map(0);

                var cbvSrvUavHandle = CbvSrvUavHeap.CPUDescriptorHandleForHeapStart;

                // 入力値を収めたバッファを参照する SRV を作成します。
                var srvDesc = new ShaderResourceViewDescription()
                {
                    Format = Format.Unknown,
                    Dimension = ShaderResourceViewDimension.Buffer,
                    Shader4ComponentMapping = D3DXUtilities.DefaultComponentMapping(),
                    Buffer =
                    {
                        FirstElement = 0,
                        ElementCount = ComputeProcessCount,
                        StructureByteStride = Utilities.SizeOf<ComputeInputStruct>(),
                        Flags = BufferShaderResourceViewFlags.None,
                    },
                };
                Device.CreateShaderResourceView(ComputeInputBuffer, srvDesc, cbvSrvUavHandle);
                cbvSrvUavHandle += CbvSrvUavDescriptorSize;

                // 結果を収めるバッファを参照する UAV を作成します。
                var uavDesc = new UnorderedAccessViewDescription()
                {
                    Format = Format.Unknown,
                    Dimension = UnorderedAccessViewDimension.Buffer,
                    Buffer =
                    {
                        FirstElement = 0,
                        ElementCount = ComputeProcessCount,
                        StructureByteStride = Utilities.SizeOf<ComputeOutputStruct>(),
                        CounterOffsetInBytes = 0,
                        Flags = BufferUnorderedAccessViewFlags.None,
                    },
                };
                Device.CreateUnorderedAccessView(ComputeOutputBuffer, null, uavDesc, cbvSrvUavHandle);
                cbvSrvUavHandle += CbvSrvUavDescriptorSize;
            }

            // 同期オブジェクトの作成
            {
                Fence = Device.CreateFence(0, FenceFlags.None);
                ComputeFence = Device.CreateFence(0, FenceFlags.None);
                FenceValue = 1;

                FenceEvent = new AutoResetEvent(false);
            }
        }

        internal void Update()
        {
            // 入力データを更新します。
            for(var i = 0; i < ComputeProcessCount; i++)
            {
                ComputeInputData[i].number++;
            }

            // アップロード用のバッファを更新します。
            var currentPtr = ComputeInputBufferUploadPtr;
            for (var i = 0; i < ComputeProcessCount; i++)
            {
                currentPtr = Utilities.WriteAndPosition(currentPtr, ref ComputeInputData[i]);
            }
        }

        internal void Render()
        {
            PopulateCommandList();

            {
                // コンピュート処理を実行します。
                ComputeCommandQueue.ExecuteCommandList(ComputeCommandList);

                // コンピュート処理のあとにグラフィック処理が実行されるようにします。
                ComputeCommandQueue.Signal(ComputeFence, FenceValue);
                CommandQueue.Wait(ComputeFence, FenceValue);
            }

            // グラフィック処理を実行します。
            CommandQueue.ExecuteCommandList(CommandList);

            SwapChain.Present(1, PresentFlags.None);

            WaitForPreviousFrame();

            Verify();
        }

        /// <summary>
        /// コンピュート処理の結果を検証します。
        /// </summary>
        private void Verify()
        {
            var outputs = new ComputeOutputStruct[ComputeProcessCount];

            // 出力バッファの内容を読み出します。
            var currentComputeOutputBufferPtr = ComputeOutputBufferUploadPtr;
            for (var i = 0; i < ComputeProcessCount; i++)
            {
                currentComputeOutputBufferPtr = Utilities.ReadAndPosition(currentComputeOutputBufferPtr, ref outputs[i]);
            }

            // 出力バッファの内容を検証します。
            for (var i = 0; i < ComputeProcessCount; i++)
            {
                var expected = ComputeInputData[i].number * ComputeInputData[i].number;

                Debug.Assert(outputs[i].result == expected);
            }
        }

        private void PopulateCommandList()
        {
            // それぞれのコマンドアロケータをリセット
            CommandAllocator.Reset();
            ComputeCommandAllocator.Reset();

            // それぞれのコマンドリストをリセット
            CommandList.Reset(CommandAllocator, null);
            ComputeCommandList.Reset(ComputeCommandAllocator, ComputePipelineState);

            // コンピュート処理の積み込み
            {
                ComputeCommandList.SetComputeRootSignature(ComputeRootSignature);

                ComputeCommandList.SetDescriptorHeaps(1, new[] { CbvSrvUavHeap });

                ComputeCommandList.SetComputeRootDescriptorTable(0, CbvSrvUavHeap.GPUDescriptorHandleForHeapStart);

                // 入力バッファを更新します。
                ComputeCommandList.CopyBufferRegion(ComputeInputBuffer, 0, ComputeInputBufferUpload, 0, InputBufferSize);

                // コンピュート処理を実行します。
                ComputeCommandList.Dispatch(ComputeProcessCount / ThreadBlockSize, 1, 1);

                // 結果を CPU アクセス可能なバッファにコピーします。
                ComputeCommandList.CopyBufferRegion(ComputeOutputBufferUpload, 0, ComputeOutputBuffer, 0, OutputBufferSize);
            }
            ComputeCommandList.Close();

            // グラフィック処理の積み込み
            {
                CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

                var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
                rtvDescHandle += FrameIndex * RtvDescriptorSize;

                // 画面をクリアするだけ
                CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

                CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);
            }
            CommandList.Close();
        }

        private void WaitForPreviousFrame()
        {
            var fence = FenceValue;

            CommandQueue.Signal(Fence, fence);
            FenceValue++;

            if (Fence.CompletedValue < fence)
            {
                Fence.SetEventOnCompletion(fence, FenceEvent.SafeWaitHandle.DangerousGetHandle());
                FenceEvent.WaitOne();
            }

            FrameIndex = SwapChain.CurrentBackBufferIndex;
        }
    }
}