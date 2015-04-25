// SpringSocket.cs - Elliot Hatch and Samuel Davidson - November 2014
// Updated April 2015 by Samuel Davidson
using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace StringSocket
{
    /// <summary> 
    /// A StringSocket is a wrapper around a Socket.  It provides methods that
    /// asynchronously read lines of text (strings terminated by newlines) and 
    /// write strings. (As opposed to Sockets, which read and write raw bytes.)  
    ///
    /// StringSockets are thread safe.  This means that two or more threads may
    /// invoke methods on a shared StringSocket without restriction.  The
    /// StringSocket takes care of the synchronization.
    /// 
    /// Each StringSocket contains a Socket object that is provided by the client.  
    /// A StringSocket will work properly only if the client refrains from calling
    /// the contained Socket's read and write methods.
    /// 
    /// If we have an open Socket s, we can create a StringSocket by doing
    /// 
    ///    StringSocket ss = new StringSocket(s, new UTF8Encoding());
    /// 
    /// We can write a string to the StringSocket by doing
    /// 
    ///    ss.BeginSend("Hello world", callback, payload);
    ///    
    /// where callback is a SendCallback (see below) and payload is an arbitrary object.
    /// This is a non-blocking, asynchronous operation.  When the StringSocket has 
    /// successfully written the string to the underlying Socket, or failed in the 
    /// attempt, it invokes the callback.  The parameters to the callback are a
    /// (possibly null) Exception and the payload.  If the Exception is non-null, it is
    /// the Exception that caused the send attempt to fail.
    /// 
    /// We can read a string from the StringSocket by doing
    /// 
    ///     ss.BeginReceive(callback, payload)
    ///     
    /// where callback is a ReceiveCallback (see below) and payload is an arbitrary object.
    /// This is non-blocking, asynchronous operation.  When the StringSocket has read a
    /// string of text terminated by a newline character from the underlying Socket, or
    /// failed in the attempt, it invokes the callback.  The parameters to the callback are
    /// a (possibly null) string, a (possibly null) Exception, and the payload.  Either the
    /// string or the Exception will be non-null, but nor both.  If the string is non-null, 
    /// it is the requested string (with the newline removed).  If the Exception is non-null, 
    /// it is the Exception that caused the send attempt to fail.
    /// </summary>

    public class StringSocket
    {
        // These delegates describe the callbacks that are used for sending and receiving strings.
        public delegate void SendCallback(Exception e, object payload);
        public delegate void ReceiveCallback(String s, Exception e, object payload);
        public delegate void DisconnectEvent(Exception e);

        // Containers for this StringSocket's socket and encoding. Value is set during the constructor.
        private Socket m_socket;
        private Encoding m_encoding;

        //Member variables used for sending data.
        //sendQueue is used to queue up data that has been requested to be sent. This is so that all data is sent and that it is sent in order.
        private Queue<SendData> m_sendQueue;
        //bytesSent keeps track of how many bytes of the current string have been sent. Reset after each string is sent.
        private int m_bytesSent;

        //Member variables used for receiving data.
        //receiveQueue keeps the call back and payload of receive request in the order they were received. 
        //As soon as message is received, the receiveQueue dequeues and the callback is called with the payload.
        private Queue<ReceiveData> m_receiveQueue;
        //receiveBuffer contains all received bytes from the most previous socket receive. The bytes within are then parsed for data.
        private byte[] m_receiveBuffer;
        //unrequestedStringQueue is used when more data is received from the socket than is requested by the user. 
        //The extra data is then stored in this queue until it is requested by the user and will be quickly dequeued.
        private Queue<string> m_unrequestedStringQueue;
        //receivedString is the a byte list representing the received string that is parsed from the receivedBuffer. Sends with the payload.
        private List<byte> m_receivedString;

        //isClosed is used when the user requests for the StringSocket to be closed. 
        //The sending of messages checks if isClosed is true, so it will close the socket as soon as the last message is sent.
        private bool m_isClosed;

        //Called when the socket is unexpectedly disconnected
        public event DisconnectEvent OnUnexpectedDisconnect;


        /// <summary>
        /// Creates a StringSocket from a regular Socket, which should already be connected.  
        /// The read and write methods of the regular Socket must not be called after the
        /// LineSocket is created.  Otherwise, the StringSocket will not behave properly.  
        /// The encoding to use to convert between raw bytes and strings is also provided.
        /// </summary>
        public StringSocket(Socket s, Encoding e)
        {
            m_sendQueue = new Queue<SendData>();
            m_bytesSent = 0;

            m_receiveQueue = new Queue<ReceiveData>();
            m_receiveBuffer = new byte[1024];
            m_unrequestedStringQueue = new Queue<string>();
            m_receivedString = new List<byte>();

            m_socket = s;
            m_encoding = e;
            m_isClosed = false;

        }

        /// <summary>
        /// We can write a string to a StringSocket ss by doing
        /// 
        ///    ss.BeginSend("Hello world", callback, payload);
        ///    
        /// where callback is a SendCallback (see below) and payload is an arbitrary object.
        /// This is a non-blocking, asynchronous operation.  When the StringSocket has 
        /// successfully written the string to the underlying Socket, or failed in the 
        /// attempt, it invokes the callback.  The parameters to the callback are a
        /// (possibly null) Exception and the payload.  If the Exception is non-null, it is
        /// the Exception that caused the send attempt to fail. 
        /// 
        /// This method is non-blocking.  This means that it does not wait until the string
        /// has been sent before returning.  Instead, it arranges for the string to be sent
        /// and then returns.  When the send is completed (at some time in the future), the
        /// callback is called on another thread.
        /// 
        /// This method is thread safe.  This means that multiple threads can call BeginSend
        /// on a shared socket without worrying around synchronization.  The implementation of
        /// BeginSend must take care of synchronization instead.  On a given StringSocket, each
        /// string arriving via a BeginSend method call must be sent (in its entirety) before
        /// a later arriving string can be sent.
        /// </summary>
        public void BeginSend(String s, SendCallback callback, object payload)
        {
            byte[] sendBuffer = m_encoding.GetBytes(s);
            lock (m_sendQueue)
            {
                m_sendQueue.Enqueue(new SendData(sendBuffer, callback, payload));
                if (m_sendQueue.Count == 1)
                {
                    try
                    {
                        //begin sending if this is the only string in the queue
                        m_socket.BeginSend(sendBuffer, 0, sendBuffer.Length, SocketFlags.None, sendCallback, null);
                    }
                    catch (Exception e)
                    {
                        m_sendQueue.Dequeue();
                        callback(e, payload);
                    }
                }
            }
        }

        /// <summary>
        /// Private callback function for the socket.BeginSend function.
        /// This function checks if all the data the user requested to be sent is sent. 
        /// If not, socket.BeginSend is called on the remaining data and an event loop is formed until all of the data is sent.
        /// </summary>
        /// <param name="ar">State Data</param>
        private void sendCallback(IAsyncResult ar)
        {
            int newBytesSent;
            SendData sendData = m_sendQueue.Peek();
            try
            {
                newBytesSent = m_socket.EndSend(ar);
            }
            catch (Exception e)
            {
                //the send failed, call the callback with the exception
                m_sendQueue.Dequeue();
                sendData.m_callback(e, sendData.m_payload);
                return;
            }

            m_bytesSent += newBytesSent;

            if (m_bytesSent < sendData.m_buffer.Length)
            {
                //if the entire string was not sent, resend the rest of the data
                m_socket.BeginSend(sendData.m_buffer, m_bytesSent, sendData.m_buffer.Length, SocketFlags.None, sendCallback, null);
            }
            else
            {
                lock (m_sendQueue)
                {
                    //send succeeded, call the callback
                    Task callbackTask = Task.Factory.StartNew(() => { sendData.m_callback(null, sendData.m_payload); });
                    m_sendQueue.Dequeue();
                    m_bytesSent = 0;
                    if (m_sendQueue.Count > 0)
                    {
                        //if there are more strings in the queue, begin sending the next one
                        SendData newSendData = m_sendQueue.Peek();
                        m_socket.BeginSend(newSendData.m_buffer, m_bytesSent, newSendData.m_buffer.Length, SocketFlags.None, sendCallback, null);
                    }
                    else if (m_isClosed)
                    {
                        m_socket.Shutdown(SocketShutdown.Send);
                        m_socket.Close();
                    }
                }
            }

        }

        /// <summary>
        /// 
        /// <para>
        /// We can read a string from the StringSocket by doing
        /// </para>
        /// 
        /// <para>
        ///     ss.BeginReceive(callback, payload)
        /// </para>
        /// 
        /// <para>
        /// where callback is a ReceiveCallback (see below) and payload is an arbitrary object.
        /// This is non-blocking, asynchronous operation.  When the StringSocket has read a
        /// string of text terminated by a newline character from the underlying Socket, or
        /// failed in the attempt, it invokes the callback.  The parameters to the callback are
        /// a (possibly null) string, a (possibly null) Exception, and the payload.  Either the
        /// string or the Exception will be non-null, but nor both.  If the string is non-null, 
        /// it is the requested string (with the newline removed).  If the Exception is non-null, 
        /// it is the Exception that caused the send attempt to fail.
        /// </para>
        /// 
        /// <para>
        /// This method is non-blocking.  This means that it does not wait until a line of text
        /// has been received before returning.  Instead, it arranges for a line to be received
        /// and then returns.  When the line is actually received (at some time in the future), the
        /// callback is called on another thread.
        /// </para>
        /// 
        /// <para>
        /// This method is thread safe.  This means that multiple threads can call BeginReceive
        /// on a shared socket without worrying around synchronization.  The implementation of
        /// BeginReceive must take care of synchronization instead.  On a given StringSocket, each
        /// arriving line of text must be passed to callbacks in the order in which the corresponding
        /// BeginReceive call arrived.
        /// </para>
        /// 
        /// <para>
        /// Note that it is possible for there to be incoming bytes arriving at the underlying Socket
        /// even when there are no pending callbacks.  StringSocket implementations should refrain
        /// from buffering an unbounded number of incoming bytes beyond what is required to service
        /// the pending callbacks.        
        /// </para>
        /// 
        /// <param name="callback"> The function to call upon receiving the data</param>
        /// <param name="payload"> 
        /// The payload is "remembered" so that when the callback is invoked, it can be associated
        /// with a specific Begin Receiver....
        /// </param>  
        /// 
        /// <example>
        ///   Here is how you might use this code:
        ///   <code>
        ///                    client = new TcpClient("localhost", port);
        ///                    Socket       clientSocket = client.Client;
        ///                    StringSocket receiveSocket = new StringSocket(clientSocket, new UTF8Encoding());
        ///                    receiveSocket.BeginReceive(CompletedReceive1, 1);
        /// 
        ///   </code>
        /// </example>
        /// </summary>
        /// 
        /// 
        public void BeginReceive(ReceiveCallback callback, object payload)
        {
            lock (m_receiveQueue)
            {
                if (m_unrequestedStringQueue.Count > 0)
                {
                    //if we've stored any strings from previous recieves, use one and don't request from the network
                    string outboundString = m_unrequestedStringQueue.Dequeue();
                    Task callbackTask = Task.Factory.StartNew(
                        () => { callback(outboundString, null, payload); });
                    return;
                }

                m_receiveQueue.Enqueue(new ReceiveData(callback, payload));
                if (m_receiveQueue.Count == 1)
                {
                    try
                    {
                        //if this is the first receive in the queue, begin receiving bytes
                        m_socket.BeginReceive(m_receiveBuffer, 0, m_receiveBuffer.Length, SocketFlags.None, receiveCallback, null);
                    }
                    catch (Exception e)
                    {
                        m_receiveQueue.Dequeue();
                        callback(null, e, payload);
                    }
                }
            }
        }
        /// <summary>
        /// Private callback function for the socket.BeginReceive function.
        /// When this function is called the socket has received data and here it is processed for newlines and if the socket has been closed.
        /// If the socket received more data than the user requested, it is stored in the unrequeuestedStringQueue to be sent out at a later time when the user requests it.
        /// </summary>
        /// <param name="ar">State Data</param>
        private void receiveCallback(IAsyncResult ar)
        {
            if (m_isClosed) // Check if we are closed. If so, stop the receive process.
            {
                return;
            }
            int newBytesRecieved;
            try
            {
                newBytesRecieved = m_socket.EndReceive(ar);
            }
            catch (Exception e)
            {
                //receive failed, call the callback with the exception
                ReceiveData receiveData = m_receiveQueue.Dequeue();
                receiveData.m_callback(null, e, receiveData.m_payload);
                return;
            }
            if (newBytesRecieved == 0)
                Close();

            for (int i = 0; i < newBytesRecieved; i++)
            {
                if (m_receiveBuffer[i] != '\n')
                {
                    m_receivedString.Add(m_receiveBuffer[i]);
                }
                else
                {
                    //found a new line
                    String outboundString = "" + m_encoding.GetString(m_receivedString.ToArray());
                    lock (m_receiveQueue)
                    {
                        if (m_receiveQueue.Count > 0)
                        {
                            //if there are any receives in the queue, call the receive callback 
                            ReceiveData receiveData = m_receiveQueue.Dequeue();
                            Task callbackTask = Task.Factory.StartNew(
                                () => receiveData.m_callback(outboundString, null, receiveData.m_payload));
                        }
                        else
                        {
                            //if there aren't any pending receives, store the extra strings we received locally
                            m_unrequestedStringQueue.Enqueue(outboundString);
                        }
                    }
                    m_receivedString.Clear();
                }
            }

            if (m_receivedString.Count > 0 || m_receiveQueue.Count > 0)
            {
                //if we haven't found a newline, or there are pending receives, receive more bytes
                try
                {
                    m_socket.BeginReceive(m_receiveBuffer, 0, m_receiveBuffer.Length, SocketFlags.None, receiveCallback, null);
                }
                catch (Exception e)
                {
                    //Lost connection.
                    if (OnUnexpectedDisconnect != null)
                    {
                        Task.Factory.StartNew(() => OnUnexpectedDisconnect(e));
                    }
                }

            }
        }

        /// <summary>
        /// Calling the close method will close the String Socket (and the underlying
        /// standard socket).  The close method  should make sure all 
        ///
        /// Note: ideally the close method should make sure all pending data is sent
        ///       
        /// Note: closing the socket should discard any remaining (inbound) messages and       
        ///       disable receiving new messages
        /// 
        /// Note: Make sure to shutdown the socket before closing it.
        ///
        /// Note: the socket should not be used after closing.
        /// </summary
        public void Close()
        {
            m_socket.Shutdown(SocketShutdown.Receive);
            m_isClosed = true;
            try
            {
                m_socket.Close();
            }
            catch (Exception)
            {
                //The socket is already closed and that is alright!
            }

        }
        /// <summary>
        /// Private SendData class that is used for queuing up data that the user requests to send.
        /// Contains all the three properties of a send request, the bytes, callback, and payload.
        /// When the SendData is dequeued from the sendQueue, it is send to the socket to be sent and its callback is called.
        /// </summary>
        private class SendData
        {
            //Containers for the send request data.
            public byte[] m_buffer;
            public SendCallback m_callback;
            public object m_payload;
            /// <summary>
            /// Constructor that stores the send request's parameters.
            /// </summary>
            /// <param name="buffer">List of bytes to be sent</param>
            /// <param name="callback">Callback function to be called upon completion</param>
            /// <param name="payload">Payload identifier of the callback</param>
            public SendData(byte[] buffer, SendCallback callback, object payload)
            {
                m_buffer = buffer;
                m_callback = callback;
                m_payload = payload;
            }
        }
        /// <summary>
        /// Private ReveiveData class that is used for queueing up data that the user requests to receive.
        /// Contains the two properties of a send request, the callback and payload.
        /// When the ReceiveData is dequeued from the receiveQueue, the callback is called with the payload and bytes received.
        /// </summary>
        private class ReceiveData
        {
            //Containers for the receive request's data.
            public ReceiveCallback m_callback;
            public object m_payload;
            /// <summary>
            /// Constructor that stores the receive request's parameters.
            /// </summary>
            /// <param name="callback">Callback function to be called upon completion</param>
            /// <param name="payload">Payload identifier of the callback</param>
            public ReceiveData(ReceiveCallback callback, object payload)
            {
                m_callback = callback;
                m_payload = payload;
            }
        }

    }
}