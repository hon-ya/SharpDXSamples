﻿using System;
using SharpDX.Windows;

namespace D3D12HelloWindow
{
    static class Program
    {
        /// <summary>
        /// アプリケーションのメイン エントリ ポイントです。
        /// </summary>
        [STAThread]
        static void Main()
        {
            var form = new RenderForm("D3D12 Hello Window")
            {
                ClientSize = new System.Drawing.Size
                {
                    Width = 1280,
                    Height = 720,
                },
            };
            form.Show();

            using (var app = new D3D12HelloWindow())
            {
                app.Initialize(form);

                using (var loop = new RenderLoop(form))
                {
                    while(loop.NextFrame())
                    {
                        app.Update();
                        app.Render();
                    }
                }
            }
        }
    }
}
