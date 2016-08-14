﻿using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lithnet.Pan.RAProxy.RadiusAccounting;
using System.Threading;

namespace Lithnet.Pan.RAProxy
{
    internal class AccountingListener
    {
        private bool shutdown;

        private ManualResetEvent _receiveDone = new ManualResetEvent(false);

        public int Port { get; set; }

        public AccountingListener()
        {
            this.Port = 1813;
        }

        /// <summary>
        /// Instantiate the listener service on the designated host and port
        /// </summary>
        public AccountingListener(int usePort = 1813)
        {
            this.Port = usePort;

        }

        public void Start()
        {
            // Create a TCP/IP socket for listener
            UdpClient listener = new UdpClient(this.Port);

            // Listen for incoming connections.
            try
            {
                Debug.WriteLine($"Server listening on port {this.Port}.");

                while (!this.shutdown)
                {
                    _receiveDone.Reset();
                    listener.BeginReceive(new AsyncCallback(ReceiveCallback), listener);

                    _receiveDone.WaitOne();
                   
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.ToString());
            }

            listener.Close();
        }

        /// <summary>
        /// Event handler method to be fired when the socket receives a data packet.
        /// </summary>
        /// <param name="asyncResult">State variable passed from asynchronous receive</param>
        private void ReceiveCallback(IAsyncResult asyncResult)
        {
            // UDP client
            UdpClient listener = (UdpClient)asyncResult.AsyncState;

            // End point
            IPEndPoint sourceEP = new IPEndPoint(IPAddress.Any, this.Port);

            byte[] receiveByteArray = listener.EndReceive(asyncResult, ref sourceEP);
            Debug.WriteLine($"Received packet from {sourceEP.Address}:{sourceEP.Port}");

            // If this is a valid sized RADIUS packet, try to parse, otherwise silently ignore
            if (receiveByteArray?.Length >= 20)
            {
                byte[] response = ParseRequestMessage(receiveByteArray, sourceEP.Address);

                if (response?.Length > 0)
                {
                    Socket sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sendSocket.SendTo(response, sourceEP);
                }
            }
        }

        public void Stop()
        {
            this.shutdown = true;
        }

        /// <summary>
        /// Handle a received AccountingRequest packet, passing any attributes to the necessary
        /// recording function and creating an acknowledgement response if the request was
        /// valid. If the request cannot be parsed or fails authentication, no response will
        /// be returned
        /// </summary>
        /// <param name="data">Incoming data packet</param>
        /// <param name="sender">Source IP address</param>
        /// <returns>Acknowledgement response data, if successfully parsed</returns>
        private static byte[] ParseRequestMessage(byte[] data, IPAddress sender)
        {
            byte requestType = data[0];                         // Type code is first 8 bits, 4 = AccountingRequest, 5 = AccountingResponse
            byte requestIdentifier = data[1];                   // Identifier is next 8 bits, representing sequence of message
            int requestLength = (data[2] << 8) | data[3];       // Length is next 16 bits, representing packet length

            // Determine if the packet contains Accounting-Request type code (4), otherwise do nothing
            if (requestType != 4)
            {
                Debug.WriteLine(" - Ignored: Not AccountingRequest type.");
                return null;
            }
            Debug.WriteLine($" - AccountingRequest #{requestIdentifier} with length {requestLength}.");

            // Check the authenticator token matches the shared secret, otherwise do nothing
            if (!AuthenticateRequest(data, sender))
            {
                Debug.WriteLine(" - Ignored: Invalid Authenticator Token.");
                return null;
            }

            // We're all good, store the attributes.
            List<RadiusAttribute> attributes = RadiusAttribute.ParseAttributeMessage(data, 20);
            foreach (var item in attributes)
            {
                Debug.WriteLine($" | " +item.ToString());
            }

            // Send the attributes array on to the necessary interface
            Program.AddToQueue(new AccountingRequest(sender, attributes));

            // Send a response acknowledgement
            byte[] responsePacket = new byte[20];
            responsePacket[0] = 5;                      // Type code is 5 for response
            responsePacket[1] = requestIdentifier;            // Identifier is the same as sent in request
            short responseLength = 20;                        // Length of response message is 2 bytes

            responsePacket[3] = (byte)(responseLength & 0xff);
            responsePacket[2] = (byte)((responseLength >> 8) & 0xff);

            // Use the request authenticator initially to authenticate the response
            Array.Copy(data, 4, responsePacket, 4, 16);
            AuthenticateResponse(responsePacket, sender);

            return responsePacket;
        }

        /// <summary>
        /// Given an AccountingRequest packet, and the sender IP address, determine if the 16 byte authenticator token
        /// included after the 4 byte header is valid.
        /// </summary>
        /// <param name="data">AccountingRequest packet</param>
        /// <param name="sender">Source IP address</param>
        /// <returns>True if the authenticator token is valid</returns>
        private static bool AuthenticateRequest(byte[] data, IPAddress sender)
        {
            // Authenticator is 16 bit MD5 sum, starting at 5th byte
            byte[] requestAuthenticator = new byte[16];         
            Array.Copy(data, 4, requestAuthenticator, 0, 16);

            // Use the sender's IP to obtain the shared secret
            string secret = Config.GetSecretForIP(sender);
            if (string.IsNullOrEmpty(secret))
                return false;

            // To obtain the MD5 authentication hash, we need to blank out the authenticator bits with zeros
            byte[] secretBytes = Encoding.ASCII.GetBytes(secret);
            byte[] hashableRequest = new byte[data.Length+secretBytes.Length];
            hashableRequest.Initialize();
            Array.Copy(data, 0, hashableRequest, 0, 4);
            Array.Copy(data, 20, hashableRequest, 20, data.Length - 20);
            Array.Copy(secretBytes, 0, hashableRequest, data.Length, secretBytes.Length);

            // Now apply the MD5 algorithm
            using (MD5 md5Hash = MD5.Create())
            {
                return requestAuthenticator.SequenceEqual(md5Hash.ComputeHash(hashableRequest));
            }
        }

        /// <summary>
        /// Modifies the AccountingResponse packet supplied so that the 16 byte request authenticator token
        /// following the 4 byte header is replaced with the calculated response authenticator token.
        /// </summary>
        /// <param name="response">Proposed response packet, with authenticator token from associated AccountingResponse packet</param>
        /// <param name="sender">Source IP address</param>
        private static void AuthenticateResponse(byte[] response, IPAddress sender)
        {
            // Authenticator token for response will be replaced based on the calculated hash
            byte[] responseAuthenticator;

            // Determine the shared secret to use from the sender's IP
            string secret = Config.GetSecretForIP(sender);
            byte[] secretBytes = Encoding.ASCII.GetBytes(secret);

            // Obtain the MD5 authentication hash
            byte[] hashableResponse = new byte[response.Length + secretBytes.Length];
            Array.Copy(response, 0, hashableResponse, 0, response.Length);
            Array.Copy(secretBytes, 0, hashableResponse, response.Length, secretBytes.Length);

            using (MD5 md5Hash = MD5.Create())
            {
                responseAuthenticator = md5Hash.ComputeHash(hashableResponse);
            }

            // Replace the response authenticator token with the calculated result
            Array.Copy(responseAuthenticator, 0, response, 4, 16);
        }
    }
}