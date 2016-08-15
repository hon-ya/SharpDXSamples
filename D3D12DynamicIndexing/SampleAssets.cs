using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace D3D12DynamicIndexing
{
    using SharpDX.Direct3D12;
    using SharpDX.DXGI;

    class SampleAssets
    {
        public const string DataFileName = "occcity.bin";
        public const int StandardVertexStride = 44;
        public const int VertexDataOffset = 524288;
        public const int VertexDataSize = 820248;
        public const int IndexDataOffset = 1344536;
        public const int IndexDataSize = 74568;
        public const Format StandardIndexFormat = Format.R32_UInt;

        static public InputElement[] StandardVertexDescription = new[]
        {
            new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
            new InputElement("TANGENT", 0, Format.R32G32B32_Float, 32, 0),
        };

		public struct TextureResource
        {
            public int Width;
            public int Height;
            public short MipLevels;
            public Format Format;

            public struct DataProperties
            {
                public int Offset;
                public int Size;
				public int Pitch;
            };

            public DataProperties[] Data;
        }

        static public TextureResource[] Textures =
        {
            new TextureResource
            {
                Width = 1024,
                Height = 1024,
                MipLevels = 1,
                Format = Format.BC1_UNorm,
                Data = new []
                {
                    new TextureResource.DataProperties
                    {
                        Offset = 0,
                        Size = 524288,
                        Pitch = 2048,
                    },
                },
            },
        };
    }
}
