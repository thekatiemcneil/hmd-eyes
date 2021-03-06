﻿using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using UnityEngine;
using Pupil;

public class PupilTools : MonoBehaviour
{
	private static PupilSettings Settings
	{
		get { return PupilSettings.Instance; }
	}

	private static EStatus _dataProcessState = EStatus.Idle;
	public static EStatus DataProcessState
	{
		get { return _dataProcessState; }
		set
		{
			_dataProcessState = value;
			if (Calibration.Marker != null)
				Calibration.Marker.SetActive (_dataProcessState == EStatus.Calibration);
		}
	}
	static EStatus stateBeforeCalibration = EStatus.Idle;

	//InspectorGUI repaint
	public delegate void GUIRepaintAction ();
	public delegate void OnCalibrationStartDeleg ();
	public delegate void OnCalibrationEndDeleg ();
	public delegate void OnCalibrationFailedDeleg ();
	public delegate void OnConnectedDelegate ();
	public delegate void OnDisconnectingDelegate ();
	public delegate void OnReceiveDataDelegate (string topic, Dictionary<string,object> dictionary);

	public static event GUIRepaintAction WantRepaint;
	public static event OnCalibrationStartDeleg OnCalibrationStarted;
	public static event OnCalibrationEndDeleg OnCalibrationEnded;
	public static event OnCalibrationEndDeleg OnCalibrationFailed;
	public static event OnConnectedDelegate OnConnected;
	public static event OnDisconnectingDelegate OnDisconnecting;
	public static event OnReceiveDataDelegate OnReceiveData;

#region Calibration

	public static void RepaintGUI ()
	{
		if (WantRepaint != null)
			WantRepaint ();
	}

	public static Connection Connection
	{
		get { return Settings.connection; }
	}
	public static bool IsConnected
	{
		get { return Connection.isConnected; }
		set { Connection.isConnected = value; }
	}
	public static IEnumerator Connect(bool retry = false, float retryDelay = 5f)
	{
		yield return new WaitForSeconds (3f);

		while (!IsConnected) 
		{
			Connection.Initialize();

			if (!IsConnected)
            {
				if (retry) 
				{
                    UnityEngine.Debug.Log("Could not connect, Re-trying in 5 seconds ! ");
					yield return new WaitForSeconds (retryDelay);

				} else 
				{
					yield return null;
				}

			} 
			//yield return null;
        }
        UnityEngine.Debug.Log(" Succesfully connected to Pupil Service ! ");

        RepaintGUI();
		if (OnConnected != null)
			OnConnected();
        yield break;
    }

	public static void SubscribeTo (string topic)
	{
		Connection.InitializeSubscriptionSocket (topic);
	}

	public static void UnSubscribeFrom (string topic)
	{
		Connection.CloseSubscriptionSocket (topic);
	}

	public static Calibration Calibration
	{
		get { return Settings.calibration; }
	}
	private static Calibration.Mode _calibrationMode = Calibration.Mode._2D;
	public static Calibration.Mode CalibrationMode
	{
		get { return _calibrationMode; }
		set 
		{
			if (_calibrationMode != value)
			{
				_calibrationMode = value;
			}
		}
	}
	public static Calibration.Type CalibrationType
	{
		get { return Calibration.currentCalibrationType; }
	}

	public static void StartCalibration ()
	{
		if (OnCalibrationStarted != null)
			OnCalibrationStarted ();
		else
		{
			print ("No 'calibration started' delegate set");
		}

		Settings.calibration.InitializeCalibration ();

		stateBeforeCalibration = DataProcessState;
		DataProcessState = EStatus.Calibration;

		byte[] calibrationData = new byte[ 1 + 2 * sizeof(ushort) + sizeof(float) ];
		calibrationData [0] = (byte) 'C';
		ushort hmdVideoFrameSize = 1000;
		byte[] frameSizeData = System.BitConverter.GetBytes (hmdVideoFrameSize);
		for (int i = 0; i < 2; i++)
		{
			for (int j = 0; j < sizeof(ushort); j++)
			{
				calibrationData [1 + i * sizeof(ushort) + j] = frameSizeData [j];
			}
		}
		float outlierThreshold = 35;
		byte[] outlierThresholdData = System.BitConverter.GetBytes (outlierThreshold);
		for (int i = 0; i < sizeof(float); i++)
		{
			calibrationData [1 + 2 * sizeof(ushort) + i] = outlierThresholdData [i];
		}
		Connection.sendData ( calibrationData );

		_calibrationData.Clear ();

		RepaintGUI ();
	}

	public static void StopCalibration ()
	{
		DataProcessState = stateBeforeCalibration;
		Settings.connection.sendCommandKey ('c');
	}

	public static void CalibrationFinished ()
	{
		DataProcessState = EStatus.Idle;

		print ("Calibration finished");

//		UnSubscribeFrom ("notify.calibration.successful");
//		UnSubscribeFrom ("notify.calibration.failed");

		if (OnCalibrationEnded != null)
			OnCalibrationEnded ();
		else
		{
			print ("No 'calibration ended' delegate set");
		}
	}

	public static void CalibrationFailed ()
	{
		DataProcessState = EStatus.Idle;

		if (OnCalibrationFailed != null)
			OnCalibrationFailed ();
		else
		{
			print ("No 'calibration failed' delegate set");
		}
	}

	private static List<Dictionary<string,object>> _calibrationData = new List<Dictionary<string,object>> ();
	public static void AddCalibrationReferenceData ()
	{
		Connection.sendRequestMessage (new Dictionary<string,object> {
			{ "subject","calibration.add_ref_data" },
			{
				"ref_data",
				_calibrationData.ToArray ()
			}
		});

		if (Settings.debug.printSampling)
		{
			print ("Sending ref_data");

			string str = "";

			foreach (var element in _calibrationData)
			{
				foreach (var i in element)
				{
					if (i.Key == "norm_pos")
					{
						str += "|| " + i.Key + " | " + ((System.Single[])i.Value) [0] + " , " + ((System.Single[])i.Value) [1];
					} else
					{
						str += "|| " + i.Key + " | " + i.Value.ToString ();
					}
				}
				str += "\n";

			}

			print (str);
		}

		//Clear the current calibration data, so we can proceed to the next point if there is any.
		_calibrationData.Clear ();
	}

	public static void AddCalibrationPointReferencePosition (float[] position, float timestamp)
	{
		if (CalibrationMode == Calibration.Mode._3D)
			for (int i = 0; i < position.Length; i++)
				position [i] *= PupilSettings.PupilUnitScalingFactor;
		
		_calibrationData.Add ( new Dictionary<string,object> () {
			{ Settings.calibration.currentCalibrationType.positionKey, position }, 
			{ "timestamp", timestamp },
			{ "id", PupilData.leftEyeID }
		});
		_calibrationData.Add ( new Dictionary<string,object> () {
			{ Settings.calibration.currentCalibrationType.positionKey, position }, 
			{ "timestamp", timestamp },
			{ "id", PupilData.rightEyeID }
		});

        if (_calibrationData.Count > 40)
            AddCalibrationReferenceData();
    }

	#endregion

	public static void Disconnect()
	{
		if (OnDisconnecting != null)
			OnDisconnecting ();
		
		if (DataProcessState == EStatus.Calibration)
			PupilTools.StopCalibration ();

		// Starting/Stopping eye process is now part of initialization process
		//PupilTools.StopEyeProcesses ();

		Connection.CloseSubscriptionSocket("gaze");

		Connection.CloseSockets();
	}

#region CurrentlyNotSupportedOnHoloLens

	public static void StartPupilServiceRecording (string path)
	{
	}

	public static void StopPupilServiceRecording ()
	{
	}

	public static void StartBinocularVectorGazeMapper ()
	{
	}

	public static void StartFramePublishing ()
	{
	}

	public static void StopFramePublishing ()
	{
	}
		
#endregion

}
