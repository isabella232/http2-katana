﻿//-----------------------------------------------------------------------
// <copyright file="ExtensionParser.cs" company="Microsoft Open Technologies, Inc.">
//Copyright © 2002-2007, The Mentalis.org Team
//Portions Copyright © Microsoft Open Technologies, Inc.
//All rights reserved.
//http://www.mentalis.org/ 
//Redistribution and use in source and binary forms, with or without modification, 
//are permitted provided that the following conditions are met:
//- Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. 
//- Neither the name of the Mentalis.org Team, 
//nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
//THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
//INCLUDING, BUT NOT LIMITED TO, 
//THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
//IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, 
//INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, 
//PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; 
//OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
//OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, 
//EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// </copyright>
//-----------------------------------------------------------------------

using System;

namespace Org.Mentalis.Security.Ssl.Shared.Extensions
{
    using Org.Mentalis.Security.BinaryHelper;

	internal sealed class ExtensionsParser
	{
        public static ExtensionList Parse(byte[] buffer, ref int currentLen, ExtensionList knownExtensions, ConnectionEnd end)
        {
            var extsList = new ExtensionList();
            int extsLen = BinaryHelper.Int16FromBytes(buffer[currentLen++], buffer[currentLen++]);
            int extOffsetEnd = currentLen + extsLen;

            while (currentLen < extOffsetEnd)
            {
                ExtensionType type = (ExtensionType)BinaryHelper.Int16FromBytes(buffer[currentLen++], buffer[currentLen++]);
                Int16 extLen = BinaryHelper.Int16FromBytes(buffer[currentLen++], buffer[currentLen++]);
               
                if (AddExtensionToResult(buffer, ref currentLen, extLen, end, knownExtensions, type, ref extsList) == false)
                    currentLen += extsLen;
            }

            return extsList;
        }

        private static bool AddExtensionToResult(byte[] buffer, ref int currentLen, Int16 extLen, ConnectionEnd end, 
                                                    ExtensionList knownExtensions, ExtensionType type, ref ExtensionList result)
        {
            foreach (var extension in knownExtensions)
            {
                if (extension.Type == type)
                {
                    result.Add(extension.Parse(buffer, ref currentLen, extLen, end));
                    return true;
                }
            }

            return false;
        }
	}
}
