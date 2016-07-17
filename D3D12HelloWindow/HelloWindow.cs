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
        public const int FrameCount = 2;

        public Device Device { get; set; }
        public CommandQueue CommandQueue { get; private set; }
        public SwapChain3 SwapChain { get; set; }
        public int FrameIndex { get; private set; }
        public DescriptorHeap RenderTargetViewHeap { get; set; }
        public int RtvDescriptorSize { get; set; }
        public Resource[] RenderTargets { get; set; } = new Resource[FrameCount];
        public CommandAllocator CommandAllocator { get; private set; }
        public GraphicsCommandList CommandList { get; private set; }
        public int FenceValue { get; private set; }
        public Fence Fence { get; private set; }
        public AutoResetEvent FenceEvent { get; private set; }

        public void Dispose()
        {
            // GPU によるリソースの参照が完了するのを待ってから、リソースの解放を始めます。
            this.WaitForPreviousFrame();

            foreach(var target in this.RenderTargets)
            {
                target.Dispose();
            }

            this.CommandAllocator.Dispose();
            this.CommandQueue.Dispose();
            this.RenderTargetViewHeap.Dispose();
            this.CommandList.Dispose();
            this.Fence.Dispose();
            this.SwapChain.Dispose();
            this.Device.Dispose();
        }

        internal void Initialize(RenderForm form)
        {
            this.LoadPipeline(form);
            this.LoadAssets();
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

            this.Device = new Device(null, SharpDX.Direct3D.FeatureLevel.Level_11_0);

            using (var factory = new Factory4())
            {
                // コマンドキューを作成します。
                var commandQueueDesc = new CommandQueueDescription(CommandListType.Direct);
                this.CommandQueue = this.Device.CreateCommandQueue(commandQueueDesc);

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
                using (var tempSwapChain = new SwapChain(factory, this.CommandQueue, swapChainDesc))
                {
                    this.SwapChain = tempSwapChain.QueryInterface<SwapChain3>();
                    this.FrameIndex = this.SwapChain.CurrentBackBufferIndex;
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
            this.RenderTargetViewHeap = this.Device.CreateDescriptorHeap(rtvHeapDesc);
            this.RtvDescriptorSize = this.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            // フレームごとにレンダーターゲットビューを作成します。
            var rtvDescHandle = this.RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            for(var i = 0; i < FrameCount; i++)
            {
                this.RenderTargets[i] = this.SwapChain.GetBackBuffer<Resource>(i);
                this.Device.CreateRenderTargetView(this.RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += this.RtvDescriptorSize;
            }

            this.CommandAllocator = this.Device.CreateCommandAllocator(CommandListType.Direct);
        }

        private void LoadAssets()
        {
            // コマンドリストを作成します。
            this.CommandList = this.Device.CreateCommandList(CommandListType.Direct, this.CommandAllocator, null);

            // コマンドリストの作成直後はレコードモードになっているので、ここでいったん閉じます。
            this.CommandList.Close();

            // 同期処理に利用するフェンスを作成します。
            this.Fence = this.Device.CreateFence(0, FenceFlags.None);
            this.FenceValue = 1;

            // フェンスの同期イベントを扱うイベントオブジェクトを作成します。
            this.FenceEvent = new AutoResetEvent(false);
        }

        internal void Update()
        {
        }

        internal void Render()
        {
            // コマンドリストにコマンドを積み込みます。
            this.PopulateCommandList();

            // コマンドリストを実行します。
            this.CommandQueue.ExecuteCommandList(this.CommandList);

            // フレームを表示します。
            this.SwapChain.Present(1, PresentFlags.None);
            
            this.WaitForPreviousFrame();
        }

        private void PopulateCommandList()
        {
            // コマンドアロケータのリセットは、このアロケータに紐付いているすべてのコマンドリストが
            // GPU から参照されていない状態になってから行う必要があります。
            this.CommandAllocator.Reset();

            // しかしながら、コマンドリストのリセットは、アロケータと異なり、GPU に実行されている最中であっても実行できます。
            this.CommandList.Reset(this.CommandAllocator, null);

            // バックバッファをレンダーターゲットにします。
            this.CommandList.ResourceBarrierTransition(this.RenderTargets[this.FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            var rtvDescHandle = this.RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += this.FrameIndex * this.RtvDescriptorSize;

            // レンダーターゲットをクリアするコマンドを積み込みます。
            this.CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

            // バックバッファをプレゼント可能状態にします。
            this.CommandList.ResourceBarrierTransition(this.RenderTargets[this.FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);

            this.CommandList.Close();
        }

        private void WaitForPreviousFrame()
        {
            // 注意：ここでは、直前に積み込んだコマンドの実行をすぐさま待ちますが、これはベストな選択ではありません。
            // より効率的な同期方法については、D3D12HelloFrameBuffering サンプルを参照してください。

            var fence = this.FenceValue;

            // シグナルし、フェンスの値をすすめます。
            // このコマンドは、これ以前にコマンドキューに詰め込んだコマンドすべての実行が完了してから実行されます。
            this.CommandQueue.Signal(this.Fence, fence);
            this.FenceValue++;

            // 前フレームの処理が完了するまで待ちます。
            if(this.Fence.CompletedValue < fence)
            {
                // フェンスの値が指定した値に達したとき、指定のイベントをシグナルします。
                this.Fence.SetEventOnCompletion(fence, this.FenceEvent.SafeWaitHandle.DangerousGetHandle());
                // シグナルされるのを待ちます。
                this.FenceEvent.WaitOne();
            }

            this.FrameIndex = this.SwapChain.CurrentBackBufferIndex;
        }
    }
}