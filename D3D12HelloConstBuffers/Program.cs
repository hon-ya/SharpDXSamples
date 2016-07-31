﻿using System;
using SharpDX.Windows;

namespace D3D12HelloConstBuffers
{
    static class Program
    {
        /// <summary>
        /// アプリケーションのメイン エントリ ポイントです。
        /// </summary>
        [STAThread]
        static void Main()
        {
            var form = new RenderForm("D3D12 Hello Constant Buffers")
            {
                Width = 1280,
                Height = 720,
            };
            form.Show();

            using (var app = new HelloConstBuffers())
            {
                app.Initialize(form);

                using (var loop = new RenderLoop(form))
                {
                    while (loop.NextFrame())
                    {
                        app.Update();
                        app.Render();
                    }
                }
            }
        }
    }
}