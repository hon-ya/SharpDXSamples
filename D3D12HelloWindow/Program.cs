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
            var form = new RenderForm("Main Window")
            {
                Width = 1280,
                Height = 960,
            };
            form.Show();

            using (var app = new HelloWindow())
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
