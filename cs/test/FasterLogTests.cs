﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FASTER.core;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using NUnit.Framework;

namespace FASTER.test
{
    [TestFixture]
    internal class FasterLogStandAloneTests
    {
        [Test]
        [Category("FasterLog")]
        [Category("Smoke")]
        public void TestDisposeReleasesFileLocksWithCompletedCommit([Values] TestUtils.DeviceType deviceType)
        {
            string path = TestUtils.MethodTestDir + "/";
            string filename = path + "TestDisposeRelease" + deviceType.ToString() + ".log";

            DirectoryInfo di = Directory.CreateDirectory(path);
            IDevice device = TestUtils.CreateTestDevice(deviceType, filename);
            FasterLog fasterLog = new FasterLog(new FasterLogSettings { LogDevice = device, SegmentSizeBits = 22, LogCommitDir = path, LogChecksum = LogChecksumType.PerEntry });

            Assert.IsTrue(fasterLog.TryEnqueue(new byte[100], out _));

            fasterLog.Commit(spinWait: true);
            fasterLog.Dispose();
            device.Dispose();
            while (true)
            {
                try
                {
                    di.Delete(recursive: true);
                    break;
                }
                catch { }
            }
        }
    }

    // This test base class allows splitting up the tests into separate fixtures that can be run in parallel
    internal class FasterLogTestBase
    {
        protected const int entryLength = 100;
        protected const int numEntries = 100000;//1000000;
        protected const int numSpanEntries = 500;  // really slows down if go too many
        protected FasterLog log;
        protected IDevice device;
        protected string path;
        protected DeviceLogCommitCheckpointManager manager;

        protected static readonly byte[] entry = new byte[100];
        protected static readonly ReadOnlySpanBatch spanBatch = new ReadOnlySpanBatch(10000);

        private bool deleteOnClose;

        protected struct ReadOnlySpanBatch : IReadOnlySpanBatch
        {
            private readonly int batchSize;
            public ReadOnlySpanBatch(int batchSize) => this.batchSize = batchSize;
            public ReadOnlySpan<byte> Get(int index) => entry;
            public int TotalEntries() => batchSize;
        }

        protected void BaseSetup(bool deleteOnClose = true)
        {
            path = TestUtils.MethodTestDir + "/";

            // Clean up log files from previous test runs in case they weren't cleaned up
            TestUtils.DeleteDirectory(path, wait: true);

            manager = new DeviceLogCommitCheckpointManager(new LocalStorageNamedDeviceFactory(deleteOnClose: deleteOnClose), new DefaultCheckpointNamingScheme(path), false);
            this.deleteOnClose = deleteOnClose;
        }

        protected void BaseTearDown()
        {
            log?.Dispose();
            log = null;
            if (!deleteOnClose)
                manager.RemoveAllCommits();
            manager?.Dispose();
            manager = null;
            device?.Dispose();
            device = null;

            TestUtils.DeleteDirectory(path);
        }

        internal class Counter
        {
            internal int count;
            private readonly FasterLog log;

            internal Counter(FasterLog fasterLog)
            {
                this.count = 0;
                this.log = fasterLog;
            }

            internal void IncrementAndMaybeTruncateUntil(long nextAddr)
            {
                this.count++;
                if (this.count % 100 == 0)
                    this.log.TruncateUntil(nextAddr);
            }

            public override string ToString() => $"{this.count}";
        }

        public enum IteratorType
        {
            AsyncByteVector,
            AsyncMemoryOwner,
            Sync,
        }

        internal static bool IsAsync(IteratorType iterType) => iterType == IteratorType.AsyncByteVector || iterType == IteratorType.AsyncMemoryOwner;

        protected async ValueTask AssertGetNext(IAsyncEnumerator<(byte[] entry, int entryLength, long currentAddress, long nextAddress)> asyncByteVectorIter,
                                              IAsyncEnumerator<(IMemoryOwner<byte> entry, int entryLength, long currentAddress, long nextAddress)> asyncMemoryOwnerIter,
                                              FasterLogScanIterator iter, byte[] expectedData = default, bool verifyAtEnd = false)
        {
            if (asyncByteVectorIter is not null)
            {
                Assert.IsTrue(await asyncByteVectorIter.MoveNextAsync());
                if (expectedData is not null)
                    Assert.IsTrue(asyncByteVectorIter.Current.entry.SequenceEqual(expectedData));

                // MoveNextAsync() would hang here waiting for more entries
                if (verifyAtEnd)
                    Assert.AreEqual(log.TailAddress, asyncByteVectorIter.Current.nextAddress);
                return;
            }

            if (asyncMemoryOwnerIter is not null)
            {
                Assert.IsTrue(await asyncMemoryOwnerIter.MoveNextAsync());
                if (expectedData is not null)
                    Assert.IsTrue(asyncMemoryOwnerIter.Current.entry.Memory.Span.ToArray().Take(expectedData.Length).SequenceEqual(expectedData));
                asyncMemoryOwnerIter.Current.entry.Dispose();

                // MoveNextAsync() would hang here waiting for more entries
                if (verifyAtEnd)
                    Assert.AreEqual(log.TailAddress, asyncMemoryOwnerIter.Current.nextAddress);
                return;
            }

            Assert.IsTrue(iter.GetNext(out byte[] result, out _, out _));
            if (expectedData is not null)
                Assert.IsTrue(result.SequenceEqual(expectedData));
            if (verifyAtEnd)
                Assert.IsFalse(iter.GetNext(out _, out _, out _));
        }

        protected static async Task LogWriterAsync(FasterLog log, byte[] entry)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            // Enter in some entries then wait on this separate thread
            await log.EnqueueAsync(entry);
            await log.EnqueueAsync(entry);
            var commitTask = await log.CommitAsync(null, token: token);
            await log.EnqueueAsync(entry);
            await log.CommitAsync(commitTask, token: token);
        }
    }

    [TestFixture]
    internal class FasterLogGeneralTests : FasterLogTestBase
    {
        [SetUp]
        public void Setup() => base.BaseSetup();

        [TearDown]
        public void TearDown() => base.BaseTearDown();

        [Test]
        [Category("FasterLog")]
        public async ValueTask FasterLogTest1([Values] LogChecksumType logChecksum, [Values] IteratorType iteratorType)
        {
            device = Devices.CreateLogDevice(path + "fasterlog.log", deleteOnClose: true);
            var logSettings = new FasterLogSettings { LogDevice = device, LogChecksum = logChecksum, LogCommitManager = manager, TryRecoverLatest = false };
            log = IsAsync(iteratorType) ? await FasterLog.CreateAsync(logSettings) : new FasterLog(logSettings);

            byte[] entry = new byte[entryLength];
            for (int i = 0; i < entryLength; i++)
                entry[i] = (byte)i;

            for (int i = 0; i < numEntries; i++)
            {
                log.Enqueue(entry);
            }
            log.Commit(true);

            // If endAddress > log.TailAddress then GetAsyncEnumerable() will wait until more entries are added.
            var endAddress = IsAsync(iteratorType) ? log.TailAddress : long.MaxValue;
            using var iter = log.Scan(0, endAddress);
            var counter = new Counter(log);
            switch (iteratorType)
            {
                case IteratorType.AsyncByteVector:
                    await foreach ((byte[] result, int _, long _, long nextAddress) in iter.GetAsyncEnumerable())
                    {
                        Assert.IsTrue(result.SequenceEqual(entry));
                        counter.IncrementAndMaybeTruncateUntil(nextAddress);
                    }
                    break;
                case IteratorType.AsyncMemoryOwner:
                    await foreach ((IMemoryOwner<byte> result, int _, long _, long nextAddress) in iter.GetAsyncEnumerable(MemoryPool<byte>.Shared))
                    {
                        Assert.IsTrue(result.Memory.Span.ToArray().Take(entry.Length).SequenceEqual(entry));
                        result.Dispose();
                        counter.IncrementAndMaybeTruncateUntil(nextAddress);
                    }
                    break;
                case IteratorType.Sync:
                    while (iter.GetNext(out byte[] result, out _, out _))
                    {
                        Assert.IsTrue(result.SequenceEqual(entry));
                        counter.IncrementAndMaybeTruncateUntil(iter.NextAddress);
                    }
                    break;
                default:
                    Assert.Fail("Unknown IteratorType");
                    break;
            }
            Assert.AreEqual(numEntries, counter.count);
        }


        internal class TestConsumer : ILogEntryConsumer
        {
            private Counter counter;
            private byte[] entry;

            internal TestConsumer(Counter counter, byte[] entry)
            {
                this.counter = counter;
                this.entry = entry;
            }
            
            public void Consume(ReadOnlySpan<byte> result, long currentAddress, long nextAddress)
            {
                Assert.IsTrue(result.SequenceEqual(entry));
                counter.IncrementAndMaybeTruncateUntil(nextAddress);
                
            }
        }
        
        [Test]
        [Category("FasterLog")]
        public void FasterLogConsumerTest([Values] LogChecksumType logChecksum)
        {
            device = Devices.CreateLogDevice(path + "fasterlog.log", deleteOnClose: true);
            var logSettings = new FasterLogSettings { LogDevice = device, LogChecksum = logChecksum, LogCommitManager = manager, TryRecoverLatest = false };
            log = new FasterLog(logSettings);

            byte[] entry = new byte[entryLength];
            for (int i = 0; i < entryLength; i++)
                entry[i] = (byte)i;

            for (int i = 0; i < numEntries; i++)
            {
                log.Enqueue(entry);
            }
            log.Commit(true);

            using var iter = log.Scan(0, long.MaxValue);
            var counter = new Counter(log);
            var consumer = new TestConsumer(counter, entry);

            while (iter.TryConsumeNext(consumer)) {}
              
            Assert.AreEqual(numEntries, counter.count);
        }
        
        [Test]
        [Category("FasterLog")]
        public async ValueTask FasterLogAsyncConsumerTest([Values] LogChecksumType logChecksum)
        {
            device = Devices.CreateLogDevice(path + "fasterlog.log", deleteOnClose: true);
            var logSettings = new FasterLogSettings { LogDevice = device, LogChecksum = logChecksum, LogCommitManager = manager, TryRecoverLatest = false };
            log = await FasterLog.CreateAsync(logSettings);

            byte[] entry = new byte[entryLength];
            for (int i = 0; i < entryLength; i++)
                entry[i] = (byte)i;

            for (int i = 0; i < numEntries; i++)
            {
                log.Enqueue(entry);
            }
            log.Commit(true);
            log.CompleteLog(true);

            using var iter = log.Scan(0, long.MaxValue);
            var counter = new Counter(log);
            var consumer = new TestConsumer(counter, entry);
            await iter.ConsumeAllAsync(consumer);
            Assert.AreEqual(numEntries, counter.count);
        }
    }


    [TestFixture]
    internal class FasterLogEnqueueTests : FasterLogTestBase
    {
        [SetUp]
        public void Setup() => base.BaseSetup();

        [TearDown]
        public void TearDown() => base.BaseTearDown();

        [Test]
        [Category("FasterLog")]
        public async ValueTask TryEnqueue1([Values] LogChecksumType logChecksum, [Values] IteratorType iteratorType)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            device = Devices.CreateLogDevice(path + "fasterlog.log", deleteOnClose: true);
            var logSettings = new FasterLogSettings { LogDevice = device, LogChecksum = logChecksum, LogCommitManager = manager, TryRecoverLatest = false };
            log = IsAsync(iteratorType) ? await FasterLog.CreateAsync(logSettings) : new FasterLog(logSettings);

            const int dataLength = 1000;
            byte[] data1 = new byte[dataLength];
            for (int i = 0; i < dataLength; i++) data1[i] = (byte)i;

            using (var iter = log.Scan(0, long.MaxValue, scanBufferingMode: ScanBufferingMode.SinglePageBuffering))
            {
                var asyncByteVectorIter = iteratorType == IteratorType.AsyncByteVector ? iter.GetAsyncEnumerable().GetAsyncEnumerator() : default;
                var asyncMemoryOwnerIter = iteratorType == IteratorType.AsyncMemoryOwner ? iter.GetAsyncEnumerable(MemoryPool<byte>.Shared).GetAsyncEnumerator() : default;
                int i = 0;
                while (i++ < 500)
                {
                    var waitingReader = iter.WaitAsync();
                    Assert.IsTrue(!waitingReader.IsCompleted);

                    while (!log.TryEnqueue(data1, out _)) ;

                    // We might have auto-committed at page boundary
                    // Ensure we don't find new entry in iterator
                    while (waitingReader.IsCompleted)
                    {
                        var _next = iter.GetNext(out _, out _, out _);
                        Assert.IsFalse(_next);
                        waitingReader = iter.WaitAsync();
                    }
                    Assert.IsFalse(waitingReader.IsCompleted);

                    await log.CommitAsync(token: token);
                    while (!waitingReader.IsCompleted) ;
                    Assert.IsTrue(waitingReader.IsCompleted);

                    await AssertGetNext(asyncByteVectorIter, asyncMemoryOwnerIter, iter, data1, verifyAtEnd: true);
                }
            }
        }

        [Test]
        [Category("FasterLog")]
        [Category("Smoke")]

        public async ValueTask TryEnqueue2([Values] LogChecksumType logChecksum, [Values] IteratorType iteratorType, [Values] TestUtils.DeviceType deviceType)
        {
            string filename = path + "TryEnqueue2" + deviceType.ToString() + ".log";
            device = TestUtils.CreateTestDevice(deviceType, filename);

            var logSettings = new FasterLogSettings { LogDevice = device, PageSizeBits = 14, LogChecksum = logChecksum, LogCommitManager = manager, SegmentSizeBits = 22, TryRecoverLatest = false };
            log = IsAsync(iteratorType) ? await FasterLog.CreateAsync(logSettings) : new FasterLog(logSettings);

            const int dataLength = 10000;
            byte[] data1 = new byte[dataLength];
            for (int i = 0; i < dataLength; i++) data1[i] = (byte)i;

            using var iter = log.Scan(0, long.MaxValue, scanBufferingMode: ScanBufferingMode.SinglePageBuffering);
            var asyncByteVectorIter = iteratorType == IteratorType.AsyncByteVector ? iter.GetAsyncEnumerable().GetAsyncEnumerator() : default;
            var asyncMemoryOwnerIter = iteratorType == IteratorType.AsyncMemoryOwner ? iter.GetAsyncEnumerable(MemoryPool<byte>.Shared).GetAsyncEnumerator() : default;

            var appendResult = log.TryEnqueue(data1, out _);
            Assert.IsTrue(appendResult);
            await log.CommitAsync();
            await iter.WaitAsync();

            await AssertGetNext(asyncByteVectorIter, asyncMemoryOwnerIter, iter, data1);

            // This no longer fails in latest TryAllocate improvement
            appendResult = log.TryEnqueue(data1, out _);
            Assert.IsTrue(appendResult);
            await log.CommitAsync();
            await iter.WaitAsync();

            switch (iteratorType)
            {
                case IteratorType.Sync:
                    Assert.IsTrue(iter.GetNext(out _, out _, out _));
                    break;
                case IteratorType.AsyncByteVector:
                    {
                        // No more hole
                        var moveNextTask = asyncByteVectorIter.MoveNextAsync();

                        // Now the data is available.
                        Assert.IsTrue(await moveNextTask);
                    }
                    break;
                case IteratorType.AsyncMemoryOwner:
                    {
                        // No more hole
                        var moveNextTask = asyncMemoryOwnerIter.MoveNextAsync();

                        // Now the data is available, and must be disposed.
                        Assert.IsTrue(await moveNextTask);
                        asyncMemoryOwnerIter.Current.entry.Dispose();
                    }
                    break;
                default:
                    Assert.Fail("Unknown IteratorType");
                    break;
            }
        }
    }

    [TestFixture]
    internal class FasterLogTruncateTests : FasterLogTestBase
    {
        [SetUp]
        public void Setup() => base.BaseSetup();

        [TearDown]
        public void TearDown() => base.BaseTearDown();

        [Test]
        [Category("FasterLog")]
        [Category("Smoke")]

        public async ValueTask TruncateUntilBasic([Values] LogChecksumType logChecksum, [Values] IteratorType iteratorType, [Values] TestUtils.DeviceType deviceType)
        {
            string filename = path + "TruncateUntilBasic" + deviceType.ToString() + ".log";
            device = TestUtils.CreateTestDevice(deviceType, filename);

            var logSettings = new FasterLogSettings { LogDevice = device, PageSizeBits = 14, LogChecksum = logChecksum, LogCommitManager = manager, SegmentSizeBits = 22, TryRecoverLatest = false };
            log = IsAsync(iteratorType) ? await FasterLog.CreateAsync(logSettings) : new FasterLog(logSettings);

            byte[] data1 = new byte[100];
            for (int i = 0; i < 100; i++) data1[i] = (byte)i;

            for (int i = 0; i < 100; i++)
            {
                log.Enqueue(data1);
            }

            Assert.AreEqual(log.BeginAddress, log.CommittedUntilAddress);
            await log.CommitAsync();

            Assert.AreEqual(log.TailAddress, log.CommittedUntilAddress);
            Assert.AreEqual(log.BeginAddress, log.CommittedBeginAddress);

            using var iter = log.Scan(0, long.MaxValue);
            var asyncByteVectorIter = iteratorType == IteratorType.AsyncByteVector ? iter.GetAsyncEnumerable().GetAsyncEnumerator() : default;
            var asyncMemoryOwnerIter = iteratorType == IteratorType.AsyncMemoryOwner ? iter.GetAsyncEnumerable(MemoryPool<byte>.Shared).GetAsyncEnumerator() : default;

            await AssertGetNext(asyncByteVectorIter, asyncMemoryOwnerIter, iter, data1);

            log.TruncateUntil(iter.NextAddress);

            Assert.AreEqual(log.TailAddress, log.CommittedUntilAddress);
            Assert.Less(log.CommittedBeginAddress, log.BeginAddress);
            Assert.AreEqual(log.BeginAddress, iter.NextAddress);

            await log.CommitAsync();

            Assert.AreEqual(log.TailAddress, log.CommittedUntilAddress);
            Assert.AreEqual(log.BeginAddress, log.CommittedBeginAddress);
        }

        [Test]
        [Category("FasterLog")]
        [Category("Smoke")]

        public async ValueTask EnqueueAndWaitForCommitAsyncBasicTest([Values] LogChecksumType logChecksum, [Values] TestUtils.DeviceType deviceType)
        {
            CancellationToken cancellationToken = default;

            ReadOnlySpanBatch spanBatch = new ReadOnlySpanBatch(numSpanEntries);

            string filename = path + "EnqueueAndWaitForCommitAsyncBasicTest" + deviceType.ToString() + ".log";
            device = TestUtils.CreateTestDevice(deviceType, filename);
            log = new FasterLog(new FasterLogSettings { LogDevice = device, PageSizeBits = 16, MemorySizeBits = 16, LogChecksum = logChecksum, LogCommitManager = manager, SegmentSizeBits = 22 });

            int headerSize = logChecksum == LogChecksumType.None ? 4 : 12;
            bool _disposed = false;
            var commit = new Thread(() => { while (!_disposed) { log.Commit(true); Thread.Sleep(1); } });

            // create the read only memory byte that will enqueue and commit async
            ReadOnlyMemory<byte> readOnlyMemoryByte = new byte[65536 - headerSize - 64];

            commit.Start();

            // 65536=page size|headerSize|64=log header - add cancellation token on end just so not assuming default on at least one 
            await log.EnqueueAndWaitForCommitAsync(new byte[65536 - headerSize - 64], cancellationToken);

            // 65536=page size|headerSize
            await log.EnqueueAndWaitForCommitAsync(new byte[65536 - headerSize]);

            // 65536=page size|headerSize
            await log.EnqueueAndWaitForCommitAsync(spanBatch);

            // 65536=page size|headerSize
            await log.EnqueueAndWaitForCommitAsync(spanBatch, cancellationToken);

            // 65536=page size|headerSize
            await log.EnqueueAndWaitForCommitAsync(readOnlyMemoryByte);

            // 65536=page size|headerSize
            await log.EnqueueAndWaitForCommitAsync(readOnlyMemoryByte, cancellationToken);

            // TO DO: Probably do more verification - could read it but in reality, if fails it locks up waiting

            _disposed = true;

            commit.Join();
        }

        [Test]
        [Category("FasterLog")]
        public async ValueTask TruncateUntil2([Values] LogChecksumType logChecksum, [Values] IteratorType iteratorType)
        {
            device = Devices.CreateLogDevice(path + "fasterlog.log", deleteOnClose: true);
            var logSettings = new FasterLogSettings { LogDevice = device, MemorySizeBits = 20, PageSizeBits = 14, LogChecksum = logChecksum, LogCommitManager = manager, TryRecoverLatest = false, AutoRefreshSafeTailAddress = true };
            log = IsAsync(iteratorType) ? await FasterLog.CreateAsync(logSettings) : new FasterLog(logSettings);

            byte[] data1 = new byte[1000];
            for (int i = 0; i < 100; i++) data1[i] = (byte)i;

            for (int i = 0; i < 100; i++)
            {
                log.Enqueue(data1);
            }
            Assert.AreEqual(log.TailAddress, log.SafeTailAddress);

            Assert.Less(log.CommittedUntilAddress, log.SafeTailAddress);

            using var iter = log.Scan(0, long.MaxValue, scanUncommitted: true);
            var asyncByteVectorIter = iteratorType == IteratorType.AsyncByteVector ? iter.GetAsyncEnumerable().GetAsyncEnumerator() : default;
            var asyncMemoryOwnerIter = iteratorType == IteratorType.AsyncMemoryOwner ? iter.GetAsyncEnumerable(MemoryPool<byte>.Shared).GetAsyncEnumerator() : default;

            switch (iteratorType)
            {
                case IteratorType.Sync:
                    while (iter.GetNext(out _, out _, out _))
                        log.TruncateUntil(iter.NextAddress);
                    Assert.AreEqual(log.SafeTailAddress, iter.NextAddress);
                    break;
                case IteratorType.AsyncByteVector:
                    {
                        while (await asyncByteVectorIter.MoveNextAsync() && asyncByteVectorIter.Current.nextAddress != log.SafeTailAddress)
                            log.TruncateUntil(asyncByteVectorIter.Current.nextAddress);
                    }
                    break;
                case IteratorType.AsyncMemoryOwner:
                    {
                        while (await asyncMemoryOwnerIter.MoveNextAsync())
                        {
                            log.TruncateUntil(asyncMemoryOwnerIter.Current.nextAddress);
                            asyncMemoryOwnerIter.Current.entry.Dispose();
                            if (asyncMemoryOwnerIter.Current.nextAddress == log.SafeTailAddress)
                                break;
                        }
                    }
                    break;
                default:
                    Assert.Fail("Unknown IteratorType");
                    break;
            }

            // Enqueue data, becomes auto-visible
            log.Enqueue(data1);

            await AssertGetNext(asyncByteVectorIter, asyncMemoryOwnerIter, iter, data1, verifyAtEnd: true);

            log.Dispose();
        }

        [Test]
        [Category("FasterLog")]
        public async ValueTask TruncateUntilPageStart([Values] LogChecksumType logChecksum, [Values] IteratorType iteratorType)
        {
            device = Devices.CreateLogDevice(path + "fasterlog.log", deleteOnClose: true);
            log = new FasterLog(new FasterLogSettings { LogDevice = device, MemorySizeBits = 20, PageSizeBits = 14, LogChecksum = logChecksum, LogCommitManager = manager, AutoRefreshSafeTailAddress = true });
            byte[] data1 = new byte[1000];
            for (int i = 0; i < 100; i++) data1[i] = (byte)i;

            for (int i = 0; i < 100; i++)
            {
                log.Enqueue(data1);
            }
            Assert.AreEqual(log.TailAddress, log.SafeTailAddress);

            Assert.Less(log.CommittedUntilAddress, log.SafeTailAddress);

            using (var iter = log.Scan(0, long.MaxValue, scanUncommitted: true))
            {
                var asyncByteVectorIter = iteratorType == IteratorType.AsyncByteVector ? iter.GetAsyncEnumerable().GetAsyncEnumerator() : default;
                var asyncMemoryOwnerIter = iteratorType == IteratorType.AsyncMemoryOwner ? iter.GetAsyncEnumerable(MemoryPool<byte>.Shared).GetAsyncEnumerator() : default;

                switch (iteratorType)
                {
                    case IteratorType.Sync:
                        while (iter.GetNext(out _, out _, out _))
                            log.TruncateUntilPageStart(iter.NextAddress);
                        Assert.AreEqual(log.SafeTailAddress, iter.NextAddress);
                        break;
                    case IteratorType.AsyncByteVector:
                        {
                            while (await asyncByteVectorIter.MoveNextAsync() && asyncByteVectorIter.Current.nextAddress != log.SafeTailAddress)
                                log.TruncateUntilPageStart(asyncByteVectorIter.Current.nextAddress);
                        }
                        break;
                    case IteratorType.AsyncMemoryOwner:
                        {
                            while (await asyncMemoryOwnerIter.MoveNextAsync())
                            {
                                log.TruncateUntilPageStart(asyncMemoryOwnerIter.Current.nextAddress);
                                asyncMemoryOwnerIter.Current.entry.Dispose();
                                if (asyncMemoryOwnerIter.Current.nextAddress == log.SafeTailAddress)
                                    break;
                            }
                        }
                        break;
                    default:
                        Assert.Fail("Unknown IteratorType");
                        break;
                }

                // Enqueue data, becomes auto-visible
                log.Enqueue(data1);

                await AssertGetNext(asyncByteVectorIter, asyncMemoryOwnerIter, iter, data1, verifyAtEnd: true);
            }
            log.Dispose();
        }


        [Test]
        [Category("FasterLog")]
        [Category("Smoke")]
        public void CommitNoSpinWait([Values] TestUtils.DeviceType deviceType)
        {
            string filename = path + "CommitNoSpinWait" + deviceType.ToString() + ".log";
            device = TestUtils.CreateTestDevice(deviceType, filename);
            log = new FasterLog(new FasterLogSettings { LogDevice = device, LogCommitManager = manager, SegmentSizeBits = 22 });

            int commitFalseEntries = 100;

            byte[] entry = new byte[entryLength];
            for (int i = 0; i < entryLength; i++)
                entry[i] = (byte)i;

            for (int i = 0; i < commitFalseEntries; i++)
            {
                log.Enqueue(entry);
            }

            //*******
            // Main point of the test ... If commit(true) (like other tests do) it waits until commit completes before moving on.
            // If set to false, it will fire and forget the commit and return immediately (which is the way this test is set up).
            // There won't be that much difference from True to False here as the True case is so quick but there can be issues
            // if start checking right after commit without giving time to commit.
            // Also, it is a good basic check to make sure it isn't crashing and that it does actually commit it
            //*******

            log.Commit(false);
            while (log.CommittedUntilAddress < log.TailAddress) Thread.Yield();

            // Read the log - Look for the flag so know each entry is unique
            int currentEntry = 0;
            using (var iter = log.Scan(0, 100_000_000))
            {
                while (iter.GetNext(out byte[] result, out _, out _))
                {
                    if (currentEntry < entryLength)
                    {
                        Assert.AreEqual((byte)currentEntry, result[currentEntry]);
                        currentEntry++;
                    }
                }
            }

            log.Dispose();

            // Make sure expected length is same as current - also makes sure that data verification was not skipped
            Assert.AreEqual(entryLength, currentEntry);

        }

        [Test]
        [Category("FasterLog")]
        [Category("Smoke")]
        public async ValueTask CommitAsyncPrevTask([Values] TestUtils.DeviceType deviceType)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            string filename = $"{path}/CommitAsyncPrevTask_{deviceType}.log";
            device = TestUtils.CreateTestDevice(deviceType, filename);
            var logSettings = new FasterLogSettings { LogDevice = device, LogCommitManager = manager, SegmentSizeBits = 22, TryRecoverLatest = false };
            log = await FasterLog.CreateAsync(logSettings);

            // make it small since launching each on separate threads 
            const int entryLength = 10;
            int expectedEntries = 3; // Not entry length because this is number of enqueues called

            // Set Default entry data
            for (int i = 0; i < entryLength; i++)
            {
                entry[i] = (byte)i;
            }

            // Enqueue and AsyncCommit in a separate thread (wait there until commit is done though).
            Task currentTask = Task.Run(() => LogWriterAsync(log, entry), token);

            // Commit to the log
            currentTask.Wait(4000, token);

            // double check to make sure finished - seen cases where timing kept running even after commit done
            bool wasCanceled = false;
            if (currentTask.Status != TaskStatus.RanToCompletion)
            {
                wasCanceled = true;
                cts.Cancel();
            }

            // Read the log to make sure all entries are put in
            int currentEntry = 0;
            using (var iter = log.Scan(0, 100_000_000))
            {
                while (iter.GetNext(out byte[] result, out _, out _))
                {
                    if (currentEntry < entryLength)
                    {
                        Assert.AreEqual((byte)currentEntry, result[currentEntry]);
                        currentEntry++;
                    }
                }
            }

            // Make sure expected entries is same as current - also makes sure that data verification was not skipped
            Assert.AreEqual(expectedEntries, currentEntry);

            // NOTE: seeing issues where task is not running to completion on Release builds
            // This is a final check to make sure task finished. If didn't then assert
            // One note - if made it this far, know that data was Enqueue and read properly, so just
            // case of task not stopping
            if (currentTask.Status != TaskStatus.RanToCompletion)
            {
                Assert.Fail($"Final Status check Failure -- Task should be 'RanToCompletion' but current Status is: {currentTask.Status}; wasCanceled = {wasCanceled}");
            }
        }


        [Test]
        [Category("FasterLog")]
        [Category("Smoke")]
        public async ValueTask RefreshUncommittedAsyncTest([Values] IteratorType iteratorType, [Values] TestUtils.DeviceType deviceType)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            string filename = path + "RefreshUncommittedAsyncTest" + deviceType.ToString() + ".log";
            device = TestUtils.CreateTestDevice(deviceType, filename);

            log = new FasterLog(new FasterLogSettings { LogDevice = device, MemorySizeBits = 20, PageSizeBits = 14, LogCommitManager = manager, SegmentSizeBits = 22, AutoRefreshSafeTailAddress = true });
            byte[] data1 = new byte[1000];
            for (int i = 0; i < 100; i++) data1[i] = (byte)i;

            for (int i = 0; i < 100; i++)
            {
                log.Enqueue(data1);
            }

            Assert.AreEqual(log.TailAddress, log.SafeTailAddress);
            Assert.Less(log.CommittedUntilAddress, log.SafeTailAddress);

            using (var iter = log.Scan(0, long.MaxValue, scanUncommitted: true))
            {
                var asyncByteVectorIter = iteratorType == IteratorType.AsyncByteVector ? iter.GetAsyncEnumerable().GetAsyncEnumerator() : default;
                var asyncMemoryOwnerIter = iteratorType == IteratorType.AsyncMemoryOwner ? iter.GetAsyncEnumerable(MemoryPool<byte>.Shared).GetAsyncEnumerator() : default;

                switch (iteratorType)
                {
                    case IteratorType.Sync:
                        while (iter.GetNext(out _, out _, out _))
                            log.TruncateUntilPageStart(iter.NextAddress);
                        Assert.AreEqual(log.SafeTailAddress, iter.NextAddress);
                        break;
                    case IteratorType.AsyncByteVector:
                        {
                            while (await asyncByteVectorIter.MoveNextAsync() && asyncByteVectorIter.Current.nextAddress != log.SafeTailAddress)
                                log.TruncateUntilPageStart(asyncByteVectorIter.Current.nextAddress);
                        }
                        break;
                    case IteratorType.AsyncMemoryOwner:
                        {
                            while (await asyncMemoryOwnerIter.MoveNextAsync())
                            {
                                log.TruncateUntilPageStart(asyncMemoryOwnerIter.Current.nextAddress);
                                asyncMemoryOwnerIter.Current.entry.Dispose();
                                if (asyncMemoryOwnerIter.Current.nextAddress == log.SafeTailAddress)
                                    break;
                            }
                        }
                        break;
                    default:
                        Assert.Fail("Unknown IteratorType");
                        break;
                }

                // Enqueue additional data item, becomes auto-visible
                log.Enqueue(data1);

                await AssertGetNext(asyncByteVectorIter, asyncMemoryOwnerIter, iter, data1, verifyAtEnd: true);
            }
            log.Dispose();
        }
    }
    
     [TestFixture]
    internal class FasterLogCustomCommitTests : FasterLogTestBase
    {
        [SetUp]
        public void Setup() => base.BaseSetup(false);

        [TearDown]
        public void TearDown() => base.BaseTearDown();

        [Test]
        [Category("FasterLog")]
        [Category("Smoke")]
        public void FasterLogSimpleCommitCookieTest([Values] bool fastCommit)
        {
            var cookie = new byte[100];
            new Random().NextBytes(cookie);
            
            device = Devices.CreateLogDevice(path + "SimpleCommitCookie" + fastCommit + ".log", deleteOnClose: true);
            var logSettings = new FasterLogSettings { LogDevice = device, LogChecksum = LogChecksumType.PerEntry, LogCommitManager = manager, FastCommitMode = fastCommit};
            log = new FasterLog(logSettings);

            byte[] entry = new byte[entryLength];
            for (int i = 0; i < entryLength; i++)
                entry[i] = (byte)i;

            for (int i = 0; i < numEntries; i++)
            {
                log.Enqueue(entry);
            }

            log.CommitStrongly(out _, out _, true, cookie);

            var recoveredLog = new FasterLog(logSettings);
            Assert.AreEqual(cookie, recoveredLog.RecoveredCookie);
            recoveredLog.Dispose();
        }

        [Test]
        [Category("FasterLog")]
        public void FasterLogManualCommitTest()
        {
            device = Devices.CreateLogDevice(path + "logManualCommitTest.log", deleteOnClose: true);
            var logSettings = new FasterLogSettings { LogDevice = device, LogChecksum = LogChecksumType.None, LogCommitManager = manager, TryRecoverLatest = false };
            log = new FasterLog(logSettings);

            byte[] entry = new byte[entryLength];
            for (int i = 0; i < entryLength; i++)
                entry[i] = (byte)i;

            for (int i = 0; i < numEntries; i++)
            {
                log.Enqueue(entry);
            }

            var cookie1 = new byte[100];
            new Random().NextBytes(cookie1);
            var commitSuccessful = log.CommitStrongly(out var commit1Addr, out _, true, cookie1, 1);
            Assert.IsTrue(commitSuccessful);
            
            for (int i = 0; i < numEntries; i++)
            {
                log.Enqueue(entry);
            }
            
            var cookie2 = new byte[100];
            new Random().NextBytes(cookie2);
            commitSuccessful = log.CommitStrongly(out var commit2Addr, out _, true, cookie2, 2);
            Assert.IsTrue(commitSuccessful);
            
            for (int i = 0; i < numEntries; i++)
            {
                log.Enqueue(entry);
            }
            
            var cookie6 = new byte[100];
            new Random().NextBytes(cookie6);
            commitSuccessful = log.CommitStrongly(out var commit6Addr, out _, true, cookie6, 6);
            Assert.IsTrue(commitSuccessful);

            var recoveredLog = new FasterLog(logSettings);
            recoveredLog.Recover(1);
            Assert.AreEqual(cookie1, recoveredLog.RecoveredCookie);
            Assert.AreEqual(commit1Addr, recoveredLog.TailAddress);
            recoveredLog.Dispose();
            
            recoveredLog = new FasterLog(logSettings);
            recoveredLog.Recover(2);
            Assert.AreEqual(cookie2, recoveredLog.RecoveredCookie);
            Assert.AreEqual(commit2Addr, recoveredLog.TailAddress);
            recoveredLog.Dispose();

            // recovering to a non-existent commit should throw FasterException
            try
            {
                recoveredLog = new FasterLog(logSettings);
                recoveredLog.Recover(4);
                Assert.Fail();
            }
            catch (FasterException) {}

            // Default argument should recover to most recent, with TryRecoverLatest set to true
            logSettings.TryRecoverLatest = true;
            recoveredLog = new FasterLog(logSettings);
            Assert.AreEqual(cookie6, recoveredLog.RecoveredCookie);
            Assert.AreEqual(commit6Addr, recoveredLog.TailAddress);
            recoveredLog.Dispose();
        }
    }
}
