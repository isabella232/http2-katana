﻿// Copyright © Microsoft Open Technologies, Inc.
// All Rights Reserved       
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0

// THIS CODE IS PROVIDED ON AN *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR NON-INFRINGEMENT.

// See the Apache 2 License for the specific language governing permissions and limitations under the License.
using System;
using System.Diagnostics.Contracts;

namespace Microsoft.Http2.Protocol.Framing
{
    /// <summary>
    /// DATA frame class
    /// see 12 -> 6.1.
    /// </summary>
    public class DataFrame : Frame, IEndStreamFrame, IPaddingFrame
    {
        // 2 bytes: 1 for Pad High and 1 for Pad Low fields
        private const int PadHighLowLength = 2;

        // For incoming
        public DataFrame(Frame preamble)
            : base(preamble)
        {
        }

        // For outgoing
        public DataFrame(int streamId, ArraySegment<byte> data, bool isEndStream, byte padHigh = 0, byte padLow = 0)
        {
            /* 12 -> 6.1
            DATA frames MAY also contain arbitrary padding.  Padding can be added
            to DATA frames to hide the size of messages. The total number of padding
            octets is determined by multiplying the value of the Pad High field by 256 
            and adding the value of the Pad Low field. */
            Contract.Assert(data.Array != null);

            int padLength = padHigh * 256 + padLow;
            if (padLength != 0)
            {
                Buffer = new byte[Constants.FramePreambleSize + PadHighLowLength + data.Count + padLength];
                IsPadHigh = true;
                IsPadLow = true;
                PadHigh = padHigh;
                PadLow = padLow;
                PayloadLength = PadHighLowLength + data.Count + padLength;

                System.Buffer.BlockCopy(data.Array, data.Offset, Buffer, Constants.FramePreambleSize + PadHighLowLength, data.Count);
            }
            else
            {
                Buffer = new byte[Constants.FramePreambleSize + data.Count];
                PayloadLength = data.Count;               

                System.Buffer.BlockCopy(data.Array, data.Offset, Buffer, Constants.FramePreambleSize, data.Count);
            }
           
            IsEndStream = isEndStream;
            StreamId = streamId;
        }

        public bool IsEndStream
        {
            get
            {
                return (Flags & FrameFlags.EndStream) == FrameFlags.EndStream;
            }
            set
            {
                if (value)
                {
                    Flags |= FrameFlags.EndStream;
                }
            }
        }

        public bool IsEndSegment
        {
            get
            {
                return (Flags & FrameFlags.EndSegment) == FrameFlags.EndSegment;
            }
            set
            {
                if (value)
                {
                    Flags |= FrameFlags.EndSegment;
                }
            }
        }

        public bool IsPadLow
        {
            get
            {
                return (Flags & FrameFlags.PadLow) == FrameFlags.PadLow;
            }
            set
            {
                if (value)
                {
                    Flags |= FrameFlags.PadLow;
                }
            }
        }

        public bool IsPadHigh
        {
            get
            {
                return (Flags & FrameFlags.PadHight) == FrameFlags.PadHight;
            } 
            set
            {
                if (value)
                {
                    Flags |= FrameFlags.PadHight;
                }
            }
        }

        public bool HasPadding
        {
            get { return IsPadHigh && IsPadLow; }
        }

        public byte PadHigh
        {
            get
            {
                return HasPadding ? Buffer[Constants.FramePreambleSize] : (byte) 0;
            }
            set { Buffer[Constants.FramePreambleSize] = value; }
        }

        public byte PadLow
        {
            get
            {
                return HasPadding ? Buffer[Constants.FramePreambleSize + 1] : (byte) 0;
            }
            set { Buffer[Constants.FramePreambleSize + 1] = value; }
        }

        public bool IsCompressed
        {
            get
            {
                return (Flags & FrameFlags.Compressed) == FrameFlags.Compressed;
            }
            set
            {
                if (value)
                {
                    Flags |= FrameFlags.Compressed;
                }
            }
        }      

        public ArraySegment<byte> Data
        {
            get
            {
                if (HasPadding)
                {
                    int padLength = PadHigh * 256 - PadLow;

                    return new ArraySegment<byte>(Buffer, Constants.FramePreambleSize + PadHighLowLength,
                        Buffer.Length - Constants.FramePreambleSize - padLength);
                }
                return new ArraySegment<byte>(Buffer, Constants.FramePreambleSize,
                        Buffer.Length - Constants.FramePreambleSize);
            }
        }
    }
}
