﻿using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using NetMQ;
using NetMQ.Sockets;
using MessagePack;

[Serializable]
public class Connection
{
	private bool _isConnected = false;
	public bool isConnected
	{
		get { return _isConnected; }
		set { _isConnected = value; }
	}
	public bool isAutorun = true;
	public string IP = "127.0.0.1";
	public string IPHeader = ">tcp://127.0.0.1:";
	public int PORT = 50020;
	public string subport = "59485";
	public bool isLocal = true;

	private Dictionary<string,SubscriberSocket> _subscriptionSocketForTopic;
	private Dictionary<string,SubscriberSocket> subscriptionSocketForTopic
	{
		get
		{
			if ( _subscriptionSocketForTopic == null )
				_subscriptionSocketForTopic = new Dictionary<string, SubscriberSocket>();
			return _subscriptionSocketForTopic;
		}
	}
	public RequestSocket requestSocket = null;

	private bool _contextExists = false;
	private bool contextExists
	{
		get { return _contextExists; }
		set { _contextExists = value; }
	}
	private TimeSpan timeout = new System.TimeSpan (0, 0, 1); //1sec
	public void InitializeRequestSocket()
	{
		IPHeader = ">tcp://" + IP + ":";

		Debug.Log ("Attempting to connect to : " + IPHeader + PORT);

		if (!contextExists)
		{
			AsyncIO.ForceDotNet.Force ();
			NetMQConfig.ManualTerminationTakeOver ();
			NetMQConfig.ContextCreate (true);
			contextExists = true;
		}

		requestSocket = new RequestSocket (PupilTools.Settings.connection.IPHeader + PupilTools.Settings.connection.PORT);
		requestSocket.SendFrame ("SUB_PORT");
		isConnected = requestSocket.TryReceiveFrameString (timeout, out PupilTools.Settings.connection.subport);
		if (isConnected)
		{
			CheckPupilVersion ();
			SetPupilTimestamp (Time.time);
		}
	}

	public string PupilVersion;
	public List<int> PupilVersionNumbers;
	public void CheckPupilVersion()
	{
		requestSocket.SendFrame ("v");
		if (requestSocket.TryReceiveFrameString (timeout, out PupilVersion))
		{
			var split = PupilVersion.Split ('.');
			PupilVersionNumbers = new List<int> ();
			int number;
			foreach (var item in split)
			{
				if ( int.TryParse (item, out number) )
					PupilVersionNumbers.Add (number);
			}
			Is3DCalibrationSupported ();
		}
	}
	public bool Is3DCalibrationSupported()
	{
		if (PupilVersionNumbers.Count > 0)
			if (PupilVersionNumbers [0] >= 1)
				return true;

		Debug.Log ("Pupil version below 1 detected. V1 is required for 3D calibration");
		PupilTools.Settings.calibration.currentMode = Calibration.Mode._2D;
		return false;
	}

	public void CloseSockets()
	{
		if (requestSocket != null && isConnected) 
		{
			requestSocket.Close ();
			isConnected = false;
		}

		foreach (var socketKey in subscriptionSocketForTopic.Keys)
			CloseSubscriptionSocket (socketKey);
		UpdateSubscriptionSockets ();

		TerminateContext ();
	}

	private MemoryStream mStream;
	public void InitializeSubscriptionSocket(string topic)
	{		
		if (!subscriptionSocketForTopic.ContainsKey (topic))
		{
			subscriptionSocketForTopic.Add (topic, new SubscriberSocket (IPHeader + subport));
			subscriptionSocketForTopic [topic].Subscribe (topic);

			//André: Is this necessary??
//			subscriptionSocketForTopic[topic].Options.SendHighWatermark = PupilSettings.numberOfMessages;// 6;

			subscriptionSocketForTopic[topic].ReceiveReady += (s, a) => 
			{
				int i = 0;

				NetMQMessage m = new NetMQMessage();

				while(a.Socket.TryReceiveMultipartMessage(ref m)) 
				{
					// We read all the messages from the socket, but disregard the ones after a certain point
	//				if ( i > PupilSettings.numberOfMessages ) // 6)
	//					continue;
					mStream = new MemoryStream(m[1].ToByteArray());

					string msgType = m[0].ConvertToString();

					if (PupilTools.Settings.debug.printMessageType)
						Debug.Log(msgType);

					if (PupilTools.Settings.debug.printMessage)
						Debug.Log (MessagePackSerializer.ToJson(m[1].ToByteArray()));

					switch(msgType)
					{
					case "notify.calibration.successful":
						PupilTools.Settings.calibration.currentStatus = Calibration.Status.Succeeded;
						PupilTools.CalibrationFinished();
						Debug.Log(msgType);
						break;
					case "notify.calibration.failed":
						PupilTools.Settings.calibration.currentStatus = Calibration.Status.NotSet;
						PupilTools.CalibrationFailed();
						Debug.Log(msgType);
						break;
					case "gaze":
					case "pupil.0":
					case "pupil.1":
						var dictionary = MessagePackSerializer.Deserialize<Dictionary<string,object>> (mStream);
						if (PupilTools.ConfidenceForDictionary(dictionary) > 0.6f) 
						{
							if (msgType == "gaze")
								PupilTools.gazeDictionary = dictionary;
							else if (msgType == "pupil.0")
								PupilTools.pupil0Dictionary = dictionary;
							else if (msgType == "pupil.1")
								PupilTools.pupil1Dictionary = dictionary;
						}
						break;
					default: 
						Debug.Log(msgType);
	//					foreach (var item in MessagePackSerializer.Deserialize<Dictionary<string,object>> (mStream))
	//					{
	//						Debug.Log(item.Key);
	//						Debug.Log(item.Value.ToString());
	//					}
						break;
					}

					i++;
				}
			};
		}
	}

	public void UpdateSubscriptionSockets()
	{
		foreach (var socket in subscriptionSocketForTopic)
		{
			if (socket.Value.HasIn)
				socket.Value.Poll ();
		}
		for (int i = 0; i < subscriptionSocketToBeClosed.Count; i++)
		{
			var toBeClosed = subscriptionSocketToBeClosed [i];
			if (subscriptionSocketForTopic.ContainsKey (toBeClosed))
			{
				subscriptionSocketForTopic [toBeClosed].Close ();
				subscriptionSocketForTopic.Remove (toBeClosed);
			}
			subscriptionSocketToBeClosed.Remove (toBeClosed);
		}
	}
	private List<string> subscriptionSocketToBeClosed;
	public void CloseSubscriptionSocket (string topic)
	{
		if ( subscriptionSocketToBeClosed == null )
			subscriptionSocketToBeClosed = new List<string> ();
		if (!subscriptionSocketToBeClosed.Contains (topic))
			subscriptionSocketToBeClosed.Add (topic);
	}

	public void sendRequestMessage (Dictionary<string,object> data)
	{
		if (requestSocket != null && isConnected)
		{
			NetMQMessage m = new NetMQMessage ();

			m.Append ("notify." + data ["subject"]);
			m.Append (MessagePackSerializer.Serialize<Dictionary<string,object>> (data));

			requestSocket.SendMultipartMessage (m);

			// needs to wait for response for some reason..
			receiveRequestMessage ();
		}
	}

	public NetMQMessage receiveRequestMessage ()
	{
		return requestSocket.ReceiveMultipartMessage ();
	}

	public bool updatingPupilTimestamp = false;
	private float _currentPupilTimestamp = 0;
	public float currentPupilTimestamp
	{
		get 
		{ 
			if (!updatingPupilTimestamp)
			{
				updatingPupilTimestamp = true;
				UpdatePupilTimestamp ();
			}
			return _currentPupilTimestamp;
		}
		set
		{
			_currentPupilTimestamp = value;
		}
	}
	public void UpdatePupilTimestamp ()
	{
		if (updatingPupilTimestamp)
		{
			requestSocket.SendFrame ("t");
			NetMQMessage recievedMsg = receiveRequestMessage ();
			currentPupilTimestamp = float.Parse (recievedMsg [0].ConvertToString ());
		}
	}

	public void SetPupilTimestamp(float time)
	{
		if (requestSocket != null)
		{
			requestSocket.SendFrame ("T " + time.ToString ("0.00000000"));
			receiveRequestMessage ();
		}
	}

	public void TerminateContext()
	{
		if (contextExists)
		{
			NetMQConfig.ContextTerminate (true);
			contextExists = false;
		}
	}
}