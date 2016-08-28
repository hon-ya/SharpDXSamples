using SharpDX;
using SharpDX.Direct3D12;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharpDXSample
{
    public class D3D12Utilities
    {
        public const int ComponentMappingMask = 0x7;
        public const int ComponentMappingShift = 3;
        public const int ComponentMappingAlwaysSetBitAvoidingZeromemMistakes = (1 << (ComponentMappingShift * 4));

        public static int ComponentMapping(int src0, int src1, int src2, int src3)
        {

            return ((((src0) & ComponentMappingMask) |
            (((src1) & ComponentMappingMask) << ComponentMappingShift) |
                                                                (((src2) & ComponentMappingMask) << (ComponentMappingShift * 2)) |
                                                                (((src3) & ComponentMappingMask) << (ComponentMappingShift * 3)) |
                                                                ComponentMappingAlwaysSetBitAvoidingZeromemMistakes));
        }

        public static int DefaultComponentMapping()
        {
            return ComponentMapping(0, 1, 2, 3);
        }

        public static int ComponentMapping(int ComponentToExtract, int Mapping)
        {
            return ((Mapping >> (ComponentMappingShift * ComponentToExtract) & ComponentMappingMask));
        }

        public struct SubresourceData
        {
            public byte[] Data;
            public long Offset;
            public long RowPitch;
            public long SlicePitch;
        }

        public struct MemoryCopyDestination
        {
            public IntPtr Data;
            public long Offset;
            public long RowPitch;
            public long SlicePitch;
        }

        public static void UpdateSubresources(Device device, GraphicsCommandList commandList, Resource destination, Resource intermediate, long intermediateOffset, int firstSubresource, int subresourceCount, IEnumerable<SubresourceData> sources)
        {
            var desc = destination.Description;
            var layouts = new PlacedSubResourceFootprint[subresourceCount];
            var rowCounts = new int[subresourceCount];
            var rowSizesInBytes = new long[subresourceCount];
            long totalBytes;
            device.GetCopyableFootprints(ref desc, firstSubresource, subresourceCount, intermediateOffset, layouts, rowCounts, rowSizesInBytes, out totalBytes);

            var destPtr = intermediate.Map(0);
            {
                for (var i = 0; i < subresourceCount; i++)
                {
                    var destData = new MemoryCopyDestination()
                    {
                        Data = destPtr,
                        Offset = layouts[i].Offset,
                        RowPitch = layouts[i].Footprint.RowPitch,
                        SlicePitch = layouts[i].Footprint.RowPitch * rowCounts[i],
                    };
                    MemoryCopySubresource(destData, sources.ElementAt(i), rowSizesInBytes[i], rowCounts[i], layouts[i].Footprint.Depth);
                }
            }
            intermediate.Unmap(0);

            if(destination.Description.Dimension == ResourceDimension.Buffer)
            {
                commandList.CopyBufferRegion(destination, 0, intermediate, layouts[0].Offset, layouts[0].Footprint.Width);
            }
            else
            {
                for (var i = 0; i < subresourceCount; i++)
                {
                    commandList.CopyTextureRegion(
                    new TextureCopyLocation(destination, firstSubresource + i),
                    0, 0, 0,
                    new TextureCopyLocation(intermediate, layouts[i]),
                    null
                    );
                }
            }
        }

        public static void MemoryCopySubresource(MemoryCopyDestination destination, SubresourceData source, long rowSizeInBytes, int rowCount, int sliceCount)
        {
            for (var depth = 0; depth < sliceCount; depth++)
            {
                var destSlice = IntPtr.Add(destination.Data, (int)(destination.Offset + depth * destination.SlicePitch));
                var sourceSliceOffset = source.Offset + depth * source.SlicePitch;
                for (var row = 0; row < rowCount; row++)
                {
                    Utilities.Write(
                        IntPtr.Add(destSlice, row * (int)destination.RowPitch),
                        source.Data,
                        (int)(sourceSliceOffset + row * source.RowPitch),
                        (int)rowSizeInBytes
                        );
                }
            }
        }

        public static long GetRequiredIntermediateSize(Device device, Resource destiationResource, int firstSubresource, int numSubresources)
        {
            var desc = destiationResource.Description;

            long requiredSize;
            device.GetCopyableFootprints(ref desc, firstSubresource, numSubresources, 0, null, null, null, out requiredSize);

            return requiredSize;
        }
    }
}
