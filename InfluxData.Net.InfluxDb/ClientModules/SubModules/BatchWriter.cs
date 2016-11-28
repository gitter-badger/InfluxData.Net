﻿using System.Collections.Generic;
using InfluxData.Net.Common.Enums;
using InfluxData.Net.InfluxDb.Models;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System;
using InfluxData.Net.InfluxDb.ClientModules;
using InfluxData.Net.Common.Infrastructure;

namespace InfluxData.Net.InfluxDb.ClientSubModules
{
    public class BatchWriter : IBatchWriterFactory
    {
        private IBasicClientModule _basicClientModule;
        private string _dbName;
        private string _retentionPolicy;
        private TimeUnit _precision;
        private int _interval;
        private bool _continueOnError;
        private bool _isRunning;

        /// <summary>
        /// Concurrent readings queue.
        /// <see cref="http://www.codethinked.com/blockingcollection-and-iproducerconsumercollection"/>
        /// </summary>
        private BlockingCollection<Point> _pointCollection;

        /// <summary>
        /// On batch writing error event handler.
        /// </summary>
        public event EventHandler<Exception> OnError = delegate { };

        /// <summary>
        /// Constructor used by InfluxDbClient to inject the IBasicClientModule.
        /// </summary>
        internal BatchWriter(IBasicClientModule basicClientModule)
        {
            _basicClientModule = basicClientModule;
        }

        /// <summary>
        /// Constructor used by BatchWriter to create new instances of BatchWriter (through the CreateBatchWriter() method) with
        /// IBasicClientModule from InfluxDbClient. This instance BatchWriter instance is served to the end users.
        /// </summary>
        private BatchWriter(IBasicClientModule basicClientModule, string dbName, string retenionPolicy = null, TimeUnit precision = TimeUnit.Milliseconds)
        {
            _basicClientModule = basicClientModule;
            _dbName = dbName;
            _retentionPolicy = retenionPolicy;
            _precision = precision;
            _pointCollection = new BlockingCollection<Point>();
        }

        public virtual IBatchWriter CreateBatchWriter(string dbName, string retenionPolicy = null, TimeUnit precision = TimeUnit.Milliseconds)
        {
            return new BatchWriter(_basicClientModule, dbName, retenionPolicy, precision);
        }

        public virtual void Start(int interval = 1000, bool continueOnError = false)
        {
            if (interval <= 0)
                throw new ArgumentException("Interval must be a positive int value (milliseconds)");

            _continueOnError = continueOnError;

            _interval = interval;
            _isRunning = true;
            this.EnqueueBatchWritingAsync();
        }

        public virtual void AddPoint(Point point)
        {
            _pointCollection.Add(point);
        }

        public virtual void AddPoints(IEnumerable<Point> points)
        {
            foreach (var point in points)
            {
                _pointCollection.Add(point);
            }
        }

        public virtual void Stop()
        {
            _isRunning = false;
        }

        /// <summary>
        /// Waits for the "interval" amount of time then writes all the current
        /// blocking collection points to InfluxDb and calls itself again.
        /// </summary>
        /// <returns>Task.</returns>
        private async Task EnqueueBatchWritingAsync()
        {
            if (!_isRunning)
                return;

            await Task.Delay(_interval); // TODO: should be configurable
            this.WriteBatchedPointsAsync();
            this.EnqueueBatchWritingAsync();
        }

        /// <summary>
        /// Dequeues all the current points from the blocking collection
        /// and writes them all to InfluxDb in a single request.
        /// </summary>
        /// <returns>Task.</returns>
        private async Task WriteBatchedPointsAsync()
        {
            var pointCount = _pointCollection.Count;
            IList<Point> points = new List<Point>();

            for (var i = 0; i < pointCount; i++)
            {
                Point point;
                var dequeueSuccess = _pointCollection.TryTake(out point);

                if (dequeueSuccess)
                {
                    points.Add(point);
                }
                else
                {
                    RaiseError(new InvalidOperationException("Could not dequeue the collection"));
                    return;
                }
            }

            if (points.Count > 0)
            {
                await _basicClientModule.WriteAsync(_dbName, points, _retentionPolicy, _precision).ContinueWith(p => {
                    RaiseError(p.Exception);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private void RaiseError(Exception e)
        {
            if (!_continueOnError)
                _isRunning = false;

            this.OnError(this, e);
        }
    }
}
