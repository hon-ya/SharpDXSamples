using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12HelloWindow
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;

    internal class HelloWindow : IDisposable
    {
        private const int FrameCount = 2;

        private Device Device;
        private CommandQueue CommandQueue;
        private SwapChain3 SwapChain;
        private int FrameIndex;
        private DescriptorHeap RenderTargetViewHeap;
        private int RtvDescriptorSize;
        private Resource[] RenderTargets = new Resource[FrameCount];
        private CommandAllocator CommandAllocator;
        private GraphicsCommandList CommandList;
        private int FenceValue;
        private Fence Fence;
        private AutoResetEvent FenceEvent;

        public void Dispose()
        {
            // GPU によるリソースの参照が完了するのを待ってから、リソースの解放を始めます。
            WaitForPreviousFrame();

            foreach(var target in RenderTargets)
            {
                target.Dispose();
            }

            CommandAllocator.Dispose();
            CommandQueue.Dispose();
            RenderTargetViewHeap.Dispose();
            CommandList.Dispose();
            Fence.Dispose();
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
            // デバッグレイヤを有効にします。
            {
                DebugInterface.Get().EnableDebugLayer();
            }
#endif

            Device = new Device(null, SharpDX.Direct3D.FeatureLevel.Level_11_0);

            using (var factory = new Factory4())
            {
                // コマンドキューを作成します。
                var commandQueueDesc = new CommandQueueDescription(CommandListType.Direct);
                CommandQueue = Device.CreateCommandQueue(commandQueueDesc);

                // スワップチェインを作成します。
                var swapChainDesc = new SwapChainDescription()
                {
                    BufferCount = FrameCount,
                    ModeDescription = new ModeDescription(width, height, new Rational(60, 1), Format.B8G8R8A8_UNorm),
                    Usage = Usage.RenderTargetOutput,
                    SwapEffect = SwapEffect.FlipDiscard,
                    OutputHandle = form.Handle,
                    Flags = SwapChainFlags.None,
                    SampleDescription = new SampleDescription(1, 0),
                    IsWindowed = true,
                };

                // スワップチェインを作成するときの device 引数には、キューを渡す必要があります。
                using (var tempSwapChain = new SwapChain(factory, CommandQueue, swapChainDesc))
                {
                    SwapChain = tempSwapChain.QueryInterface<SwapChain3>();
                    FrameIndex = SwapChain.CurrentBackBufferIndex;
                }

                // Alt+Enter による全画面モードへの遷移を禁止します。
                factory.MakeWindowAssociation(form.Handle, WindowAssociationFlags.IgnoreAltEnter);
            }

            // レンダーターゲットビュー用のデスクリプタヒープを作成します。
            var rtvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = FrameCount,
                Type = DescriptorHeapType.RenderTargetView,
                Flags = DescriptorHeapFlags.None,
            };
            RenderTargetViewHeap = Device.CreateDescriptorHeap(rtvHeapDesc);
            RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            // フレームごとにレンダーターゲットビューを作成します。
            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            for(var i = 0; i < FrameCount; i++)
            {
                RenderTargets[i] = SwapChain.GetBackBuffer<Resource>(i);
                Device.CreateRenderTargetView(RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += RtvDescriptorSize;
            }

            CommandAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        }

        private void LoadAssets()
        {
            // コマンドリストを作成します。
            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocator, null);

            // コマンドリストの作成直後はレコードモードになっているので、ここでいったん閉じます。
            CommandList.Close();

            // 同期処理に利用するフェンスを作成します。
            Fence = Device.CreateFence(0, FenceFlags.None);
            FenceValue = 1;

            // フェンスの同期イベントを扱うイベントオブジェクトを作成します。
            FenceEvent = new AutoResetEvent(false);
        }

        internal void Update()
        {
        }

        internal void Render()
        {
            // コマンドリストにコマンドを積み込みます。
            PopulateCommandList();

            // コマンドリストを実行します。
            CommandQueue.ExecuteCommandList(CommandList);

            // フレームを表示します。
            SwapChain.Present(1, PresentFlags.None);
            
            WaitForPreviousFrame();
        }

        private void PopulateCommandList()
        {
            // コマンドアロケータのリセットは、このアロケータに紐付いているすべてのコマンドリストが
            // GPU から参照されていない状態になってから行う必要があります。
            CommandAllocator.Reset();

            // しかしながら、コマンドリストのリセットは、アロケータと異なり、GPU に実行されている最中であっても実行できます。
            CommandList.Reset(CommandAllocator, null);

            // バックバッファをレンダーターゲットにします。
            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += FrameIndex * RtvDescriptorSize;

            // レンダーターゲットをクリアするコマンドを積み込みます。
            CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

            // バックバッファをプレゼント可能状態にします。
            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);

            CommandList.Close();
        }

        private void WaitForPreviousFrame()
        {
            // 注意：ここでは、直前に積み込んだコマンドの実行をすぐさま待ちますが、これはベストな選択ではありません。
            // より効率的な同期方法については、D3D12HelloFrameBuffering サンプルを参照してください。

            var fence = FenceValue;

            // シグナルし、フェンスの値をすすめます。
            // このコマンドは、これ以前にコマンドキューに詰め込んだコマンドすべての実行が完了してから実行されます。
            CommandQueue.Signal(Fence, fence);
            FenceValue++;

            // 前フレームの処理が完了するまで待ちます。
            if(Fence.CompletedValue < fence)
            {
                // フェンスの値が指定した値に達したとき、指定のイベントをシグナルします。
                Fence.SetEventOnCompletion(fence, FenceEvent.SafeWaitHandle.DangerousGetHandle());
                // シグナルされるのを待ちます。
                FenceEvent.WaitOne();
            }

            FrameIndex = SwapChain.CurrentBackBufferIndex;
        }
    }
}