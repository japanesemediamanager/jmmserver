// ******************************* Module Header *******************************
// Module Name:   ShokoServerNotRunningException.cs
// Project:       Shoko.CLI
// 
// MIT License
// 
// Copyright © 2020 Shoko Suite
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
// *****************************************************************************

using System;
using System.Runtime.Serialization;
using Shoko.CLI.Properties;

namespace Shoko.CLI.Exceptions
{
    /// <summary>
    ///     Represents an error in the shoko cli if the shoko server is not running after startup.
    /// </summary>
    [Serializable]
    public class ShokoServerNotRunningException : Exception
    {
        /// <summary>
        ///     Initializes a new instance of <see cref="ShokoServerNotRunningException" /> class.
        /// </summary>
        public ShokoServerNotRunningException() : base(Resources.Exceptions_ShokoServerNotRunning_Message) { }

        /// <summary>
        ///     Initializes a new instance of <see cref="ShokoServerNotRunningException" /> class.
        /// </summary>
        /// <param name="message">
        ///     The message that describes the error.
        /// </param>
        public ShokoServerNotRunningException(string? message) : base(message) { }

        /// <summary>
        ///     Initializes a new instance of <see cref="ShokoServerNotRunningException" /> class.
        /// </summary>
        /// <param name="innerException">
        ///     The exception that is the cause of the current exception, or <see langword="null" /> if no inner exception is
        ///     specified.
        /// </param>
        public ShokoServerNotRunningException(Exception? innerException) : base(Resources.Exceptions_ShokoServerNotRunning_Message, innerException) { }

        /// <summary>
        ///     Initializes a new instance of <see cref="ShokoServerNotRunningException" /> class.
        /// </summary>
        /// <param name="message">
        ///     The message that describes the error.
        /// </param>
        /// <param name="innerException">
        ///     The exception that is the cause of the current exception, or <see langword="null" /> if no inner exception is
        ///     specified.
        /// </param>
        public ShokoServerNotRunningException(string? message, Exception? innerException) : base(message, innerException) { }

        /// <inheritdoc />
        protected ShokoServerNotRunningException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }
}
