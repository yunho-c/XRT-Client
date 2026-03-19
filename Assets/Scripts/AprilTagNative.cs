using System;
using System.Runtime.InteropServices;

namespace AprilTag
{
    // Struct representing an 8-bit image to be passed completely zero-copy to the detector.
    [StructLayout(LayoutKind.Sequential)]
    public struct image_u8_t
    {
        public int width;
        public int height;
        public int stride;
        public IntPtr buf;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct zarray_t
    {
        public UIntPtr el_sz;
        public int size;
        public int alloc;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct matd_t
    {
        public uint nrows;
        public uint ncols;
        public IntPtr data; // ptr to double array
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct apriltag_detection_t
    {
        public IntPtr family; // apriltag_family_t*
        public int id;
        public int hamming;
        public float decision_margin;
        public IntPtr H; // matd_t*
        public fixed double c[2]; // center [x, y]
        public fixed double p[8]; // corners 4x2 array
    }

    public static class AprilTagNative
    {
        private const string LibraryName = "apriltag";

        // Detector Lifecycle
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr apriltag_detector_create();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void apriltag_detector_destroy(IntPtr td);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void apriltag_detector_add_family(IntPtr td, IntPtr fam);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void apriltag_detector_remove_family(IntPtr td, IntPtr fam);

        // Tag Families
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr tag36h11_create();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void tag36h11_destroy(IntPtr fam);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr tagStandard41h12_create();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void tagStandard41h12_destroy(IntPtr fam);

        // Detection
        // Here we pass the image struct by reference to avoid copying.
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr apriltag_detector_detect(IntPtr td, ref image_u8_t im_orig);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void apriltag_detections_destroy(IntPtr detections);


        // --- High Level Convenience Method for Unity --- 

        /// <summary>
        /// Reads the native zarray_t returned by the detector and marshals it into an array of C# structs.
        /// </summary>
        public static apriltag_detection_t[] GetDetections(IntPtr zarrayPtr)
        {
            if (zarrayPtr == IntPtr.Zero)
                return new apriltag_detection_t[0];

            // Dereference the array structure
            zarray_t zarray = Marshal.PtrToStructure<zarray_t>(zarrayPtr);
            
            apriltag_detection_t[] detections = new apriltag_detection_t[zarray.size];

            for (int i = 0; i < zarray.size; i++)
            {
                // Each element in zarray for tags is an apriltag_detection_t* (a pointer to the detection)
                IntPtr detectionPtr = Marshal.ReadIntPtr(zarray.data, i * IntPtr.Size);
                
                // Read the actual detection struct
                detections[i] = Marshal.PtrToStructure<apriltag_detection_t>(detectionPtr);
            }

            return detections;
        }
    }
}
