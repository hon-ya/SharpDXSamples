﻿using System;
using SharpDX.Windows;

namespace D3D12PredicationQueries
{
    static class Program
    {
        /// <summary>
        /// アプリケーションのメイン エントリ ポイントです。
        /// </summary>
        [STAThread]
        static void Main()
        {
            var form = new RenderForm("D3D12 Predication Queries")
            {
                ClientSize = new System.Drawing.Size
                {
                    Width = 1280,
                    Height = 720,
                },
            };
            form.Show();

            using (var app = new PredicationQueries())
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