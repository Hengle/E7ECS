﻿//#define TRAVEL_DEBUG

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using Unity.Entities;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Collections;
using System;

public static class Travel 
{
    public static int DataIndexOfTime(NativeArray<TravelEvent> travelEvents, float time)
    {
        for (int i = 0; i < travelEvents.Length; i++)
        {
            if (travelEvents[i].IsTimeInRange(time))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Use a special "expand out" search from remembered index. You can cache the previous search result and start from there!
    /// </summary>
    public static int DataIndexOfPosition(NativeList<TravelEvent> travelEvents, float position, ref int inputRememberIndex)
    {
        if (travelEvents.Length == 0)
        {
            return -1;
        }
        //Debug.Log($"Starting job {EventList.Length}");
        int plusIndex = -1;
        int minusIndex = 0;
        int useIndex;
        int rememberIndex = inputRememberIndex;

        for (int i = 0; i < travelEvents.Length; i++)
        {
            if (i % 2 != 0)
            {
                if ((rememberIndex - (minusIndex + 1) >= 0))
                {
                    minusIndex++;
                    useIndex = rememberIndex - minusIndex;
                }
                else
                {
                    plusIndex++;
                    useIndex = rememberIndex + plusIndex;
                }
            }
            else
            {
                if ((rememberIndex + (plusIndex + 1) < travelEvents.Length))
                {
                    plusIndex++;
                    useIndex = rememberIndex + plusIndex;
                }
                else
                {
                    minusIndex++;
                    useIndex = rememberIndex - minusIndex;
                }
            }

            if (travelEvents[useIndex].IsPositionInRange(position))
            {
                rememberIndex = useIndex;
                return useIndex;
            }
        }
        //Debug.Log("Ending with minus one");
        return -1;
    }

}

/// <summary>
/// Interval-based data storage. You can get the PREVIOUS data based on 
/// Travel can be in a job and burst compiled but not blittable as it contains `NativeList`.
/// </summary>
public struct Travel<T> : System.IDisposable where T : struct
{
    public void Dispose()
    {
        if (initialized)
        {
            travelEvents.Dispose();
            datas.Dispose();
        }
    }

    /// <summary>
    /// Only used for add default at zero check, but actually you can go to negative?
    /// </summary>
    private Bool HasEventAtZero { get; set; }

    private NativeList<T> datas;
    private NativeList<TravelEvent> travelEvents;
    private int travelRememberIndex;

    //This two are for jobs. When you Add it needs to be realloc so if possible add everything before start using.
    Bool initialized;

    public void Init()
    {
        datas = new NativeList<T>(Allocator.Persistent);
        travelEvents = new NativeList<TravelEvent>(Allocator.Persistent);
        initialized = true;
    }

    public void Clear()
    {
        HasEventAtZero = false;
        travelRememberIndex = 0;
        datas.Clear();
        travelEvents.Clear();
    }


    public T FirstData => datas.Length > 0 ? datas[0] : default(T);
    public T LastData => datas.Length > 0 ? datas[datas.Length - 1] : default(T);

    public TravelEvent FirstEvent
    {
        get
        {
            return travelEvents.Length > 0 ? travelEvents[0] : TravelEvent.INVALID;
        }
    }

    public TravelEvent LastEvent
    {
        get
        {
            return travelEvents.Length > 0 ? travelEvents[travelEvents.Length - 1] : TravelEvent.INVALID;
        }
    }

    private void SetLastEvent(TravelEvent te)
    {
        travelEvents.RemoveAtSwapBack(travelEvents.Length - 1);
        travelEvents.Add(te);
    }

    public bool NoEvent => travelEvents.Length == 0;

    /// <summary>
    /// Search until it found the interval with specified position.
    /// Note that in the case that position goes back and forth in time, it will just use the first one found.
    /// It will start the subsequent search from around the previous time's result.
    /// </summary>
    public (T data, TravelEvent travelEvent) DataEventOfPosition(float position)
    {
        //Debug.Log("Finding of " + position);
        int dataIndex = Travel.DataIndexOfPosition(travelEvents, position, ref travelRememberIndex);
        return DataEventOfPositionFromIndex(dataIndex);
    }

    public (T data, TravelEvent travelEvent) DataEventOfPositionFromIndex(int dataIndex) => dataIndex == -1 ? (default(T), TravelEvent.INVALID) : (datas[dataIndex], travelEvents[dataIndex]);

    public (T data, TravelEvent travelEvent) DataEventOfTime(float time)
    {
        int index = Travel.DataIndexOfTime(travelEvents, time);
        return index != -1 ? (datas[index], travelEvents[index]) : (default(T), TravelEvent.INVALID);
    }

    /// <summary>
    /// Adds event at position/time zero if there's nothing at zero yet.
    /// This prevents the travel returning null for all positive time and position
    /// 
    /// If you have something at zero already this does nothing.
    /// 
    /// BUT if you don't have anything at zero but have other data... it will crash
    /// Travel does not support adding a data in backward direction.
    /// </summary>
    public void AddDefaultAtZero(T data)
    {
        if (!HasEventAtZero)
        {
#if TRAVEL_DEBUG
            Debug.Log("Adding default " + data.ToString());
#endif
            Add(0, 0, data);
        }
    }

    public (T data, TravelEvent travelEvent) NextOf(TravelEvent te)
    {
        int nextIndex = te.DataIndex + 1;
        if (nextIndex < datas.Length)
        {
            return (datas[nextIndex], travelEvents[nextIndex]);
        }
        else
        {
            return (default(T), TravelEvent.INVALID);
        }
    }

    public (T data, TravelEvent travelEvent) PreviousOf(TravelEvent te)
    {
        int prevIndex = te.DataIndex - 1;
        if (prevIndex >= 0 && datas.Length > 0)
        {
            return (datas[prevIndex], travelEvents[prevIndex]);
        }
        else
        {
            return (default(T), TravelEvent.INVALID);
        }
    }

    /// <summary>
    /// TIME IS TIME ELAPSED NOT ANY TIME YOU WANT!!
    /// </summary>
    public void Add(float position, float timeElapsed, T data)
    {
#if TRAVEL_DEBUG
        Debug.Log($"Adding {position} {timeElapsed} {data.ToString()}");
#endif
        if (travelEvents.Length != 0 && timeElapsed <= 0)
        {
            throw new System.Exception($"Time elapsed must be positive, except the first one which can be zero. position : {position} timeElapsed : {timeElapsed}");
        }
        TravelEvent lastEvent = LastEvent;
        TravelEvent newTe = new TravelEvent(position, (NoEvent ? 0 : lastEvent.Time) + timeElapsed, travelEvents.Length);
        if (!NoEvent)
        {
            lastEvent.LinkToNext(newTe, this);
            //Now we have to save back the last event too?
            SetLastEvent(lastEvent);
        }

        travelEvents.Add(newTe);
        datas.Add(data);

        if (position == 0)
        {
            HasEventAtZero = true;
        }
    }
}

/// <summary>
/// To keep struct purity there's no data in here! Ask data with DataIndex from Travel!
/// (It is kind of like LinkedList, but with purely struct data.
/// </summary>
public struct TravelEvent : IEquatable<TravelEvent>
{
    //Because time must move forward just time being exact is enough?
    //Exact float ok I think
    public bool Equals(TravelEvent te) => Time == te.Time;

    public static TravelEvent INVALID => new TravelEvent(Mathf.NegativeInfinity, float.NegativeInfinity, -1);

    public bool Invalid => Position == INVALID.Position && Time == INVALID.Time;
    public bool Valid => !Invalid;

    public float Position { get; }
    /// <summary>
    /// When this is the last event, this is Mathf.Infinity
    /// </summary>
    public float PositionNext { get; private set; }

    public float Time { get; }
    /// <summary>
    /// When this is the last event, this is Mathf.Infinity
    /// </summary>
    public float TimeNext { get; private set; }

    public int DataIndex { get; }

    public TravelEvent(float absolutePosition, float time, int dataIndex)
    {
        this.Position = absolutePosition;
        this.PositionNext = Mathf.Infinity;
        this.Time = time;
        this.TimeNext = Mathf.Infinity;
        this.DataIndex = dataIndex;
    }

    internal void LinkToNext<T>(TravelEvent te, Travel<T> owningTravel) where T : struct
    {
#if TRAVEL_DEBUG
        Debug.Log($"Linking to next : {te.Time} {te.Position}");
#endif
        this.TimeNext = te.Time;
        this.PositionNext = te.Position;
        //this.Next = te;
        //te.Previous = this;
        //Debug.Log("Link result : " + this.ToString());
    }

    internal bool IsPositionInRange(float position)
    {
#if TRAVEL_DEBUG
        Debug.Log($"Is in range? {Position} - {position} - {PositionNext}");
#endif
        //return (position >= Position) && (position < PositionNext);
        return (position >= Position) && (position < PositionNext);
    }

    internal bool IsTimeInRange(float time)
    {
#if TRAVEL_DEBUG
        Debug.LogFormat($"Is time in range? {Time} - {time} - {TimeNext}");
#endif
        //return (time >= Time) && (time < TimeNext);
        return (time >= Time) && (time < TimeNext);
    }

    public override string ToString()
    {
        return string.Format($"P {Position}-{PositionNext} T {Time}-{TimeNext}");
    }
}