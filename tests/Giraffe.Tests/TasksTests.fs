module Giraffe.TasksTests

open System
open Xunit
open Giraffe.Tasks

// Tests for TaskBuilder.fs
//
// Written in 2016 by Robert Peele (humbobst@gmail.com)
//
// To the extent possible under law, the author(s) have dedicated all copyright and related and neighboring rights
// to this software to the public domain worldwide. This software is distributed without any warranty.
//
// You should have received a copy of the CC0 Public Domain Dedication along with this software.
// If not, see <http://creativecommons.org/publicdomain/zero/1.0/>.

open System.Collections
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

exception TestException of string

let require x msg = if not x then failwith msg

[<Fact>]
let testShortCircuitResult() =
    let t =
        task {
            let! x = Task.FromResult(1)
            let! y = Task.FromResult(2)
            return x + y
        }
    require t.IsCompleted "didn't short-circuit already completed tasks"
    require (t.Result = 3) "wrong result"

[<Fact>]
let testDelay() =
    let mutable x = 0
    let t =
        task {
            do! Task.Delay(50)
            x <- x + 1
        }
    require (x = 0) "task already ran"
    t.Wait()

[<Fact>]
let testNoDelay() =
    let mutable x = 0
    let t =
        task {
            x <- x + 1
            do! Task.Delay(5)
            x <- x + 1
        }
    require (x = 1) "first part didn't run yet"
    t.Wait()

[<Fact>]
let testNonBlocking() =
    let sw = Stopwatch()
    sw.Start()
    let t =
        task {
            do! Task.Yield()
            Thread.Sleep(100)
        }
    sw.Stop()
    require (sw.ElapsedMilliseconds < 50L) "sleep blocked caller"
    t.Wait()

let failtest str = raise (TestException str)

[<Fact>]
let testCatching1() =
    let mutable x = 0
    let mutable y = 0
    let t =
        task {
            try
                do! Task.Delay(0)
                failtest "hello"
                x <- 1
                do! Task.Delay(100)
            with
            | TestException msg ->
                require (msg = "hello") "message tampered"
            | _ ->
                require false "other exn type"
            y <- 1
        }
    t.Wait()
    require (y = 1) "bailed after exn"
    require (x = 0) "ran past failure"

[<Fact>]
let testCatching2() =
    let mutable x = 0
    let mutable y = 0
    let t =
        task {
            try
                do! Task.Yield() // can't skip through this
                failtest "hello"
                x <- 1
                do! Task.Delay(100)
            with
            | TestException msg ->
                require (msg = "hello") "message tampered"
            | _ ->
                require false "other exn type"
            y <- 1
        }
    t.Wait()
    require (y = 1) "bailed after exn"
    require (x = 0) "ran past failure"

[<Fact>]
let testNestedCatching() =
    let mutable counter = 1
    let mutable caughtInner = 0
    let mutable caughtOuter = 0
    let t1() =
        task {
            try
                do! Task.Yield()
                failtest "hello"
            with
            | TestException msg as exn ->
                caughtInner <- counter
                counter <- counter + 1
                raise exn
        }
    let t2 =
        task {
            try
                do! t1()
            with
            | TestException msg as exn ->
                caughtOuter <- counter
                raise exn
            | e ->
                require false (sprintf "invalid msg type %s" e.Message)
        }
    try
        t2.Wait()
        require false "ran past failed task wait"
    with
    | :? AggregateException as exn ->
        require (exn.InnerExceptions.Count = 1) "more than 1 exn"
    require (caughtInner = 1) "didn't catch inner"
    require (caughtOuter = 2) "didn't catch outer"

[<Fact>]
let testTryFinallyHappyPath() =
    let mutable ran = false
    let t =
        task {
            try
                require (not ran) "ran way early"
                do! Task.Delay(100)
                require (not ran) "ran kinda early"
            finally
                ran <- true
        }
    t.Wait()
    require ran "never ran"

[<Fact>]
let testTryFinallySadPath() =
    let mutable ran = false
    let t =
        task {
            try
                require (not ran) "ran way early"
                do! Task.Delay(100)
                require (not ran) "ran kinda early"
                failtest "uhoh"
            finally
                ran <- true
        }
    try
        t.Wait()
    with
    | _ -> ()
    require ran "never ran"

[<Fact>]
let testTryFinallyCaught() =
    let mutable ran = false
    let t =
        task {
            try
                try
                    require (not ran) "ran way early"
                    do! Task.Delay(100)
                    require (not ran) "ran kinda early"
                    failtest "uhoh"
                finally
                    ran <- true
                return 1
            with
            | _ -> return 2
        }
    require (t.Result = 2) "wrong return"
    require ran "never ran"

[<Fact>]
let testUsing() =
    let mutable disposed = false
    let t =
        task {
            use d = { new IDisposable with member __.Dispose() = disposed <- true }
            require (not disposed) "disposed way early"
            do! Task.Delay(100)
            require (not disposed) "disposed kinda early"
        }
    t.Wait()
    require disposed "never disposed"

[<Fact>]
let testUsingFromTask() =
    let mutable disposedInner = false
    let mutable disposed = false
    let t =
        task {
            use! d =
                task {
                    do! Task.Delay(50)
                    use i = { new IDisposable with member __.Dispose() = disposedInner <- true }
                    require (not disposed && not disposedInner) "disposed inner early"
                    return { new IDisposable with member __.Dispose() = disposed <- true }
                }
            require disposedInner "did not dispose inner after task completion"
            require (not disposed) "disposed way early"
            do! Task.Delay(50)
            require (not disposed) "disposed kinda early"
        }
    t.Wait()
    require disposed "never disposed"

[<Fact>]
let testUsingSadPath() =
    let mutable disposedInner = false
    let mutable disposed = false
    let t =
        task {
            try
                use! d =
                    task {
                        do! Task.Delay(50)
                        use i = { new IDisposable with member __.Dispose() = disposedInner <- true }
                        failtest "uhoh"
                        require (not disposed && not disposedInner) "disposed inner early"
                        return { new IDisposable with member __.Dispose() = disposed <- true }
                    }
                ()
            with
            | TestException msg ->
                require disposedInner "did not dispose inner after task completion"
                require (not disposed) "disposed way early"
                do! Task.Delay(50)
                require (not disposed) "disposed kinda early"
        }
    t.Wait()
    require (not disposed) "disposed thing that never should've existed"

[<Fact>]
let testForLoop() =
    let mutable disposed = false
    let wrapList =
        let raw = ["a"; "b"; "c"] |> Seq.ofList
        let getEnumerator() =
            let raw = raw.GetEnumerator()
            { new IEnumerator<string> with
                member __.MoveNext() =
                    require (not disposed) "moved next after disposal"
                    raw.MoveNext()
                member __.Current =
                    require (not disposed) "accessed current after disposal"
                    raw.Current
                member __.Current =
                    require (not disposed) "accessed current (boxed) after disposal"
                    box raw.Current
                member __.Dispose() =
                    require (not disposed) "disposed twice"
                    disposed <- true
                    raw.Dispose()
                member __.Reset() =
                    require (not disposed) "reset after disposal"
                    raw.Reset()
            }
        { new IEnumerable<string> with
            member __.GetEnumerator() : IEnumerator<string> = getEnumerator()
            member __.GetEnumerator() : IEnumerator = upcast getEnumerator()
        }
    let t =
        task {
            let mutable index = 0
            do! Task.Yield()
            for x in wrapList do
                do! Task.Yield()
                match index with
                | 0 -> require (x = "a") "wrong first value"
                | 1 -> require (x = "b") "wrong second value"
                | 2 -> require (x = "c") "wrong third value"
                | _ -> require false "iterated too far!"
                index <- index + 1
                do! Task.Yield()
            do! Task.Yield()
            return 1
        }
    t.Wait()
    require disposed "never disposed"


[<Fact>]
let testForLoopSadPath() =
    let mutable disposed = false
    let wrapList =
        let raw = ["a"; "b"; "c"] |> Seq.ofList
        let getEnumerator() =
            let raw = raw.GetEnumerator()
            { new IEnumerator<string> with
                member __.MoveNext() =
                    require (not disposed) "moved next after disposal"
                    raw.MoveNext()
                member __.Current =
                    require (not disposed) "accessed current after disposal"
                    raw.Current
                member __.Current =
                    require (not disposed) "accessed current (boxed) after disposal"
                    box raw.Current
                member __.Dispose() =
                    require (not disposed) "disposed twice"
                    disposed <- true
                    raw.Dispose()
                member __.Reset() =
                    require (not disposed) "reset after disposal"
                    raw.Reset()
            }
        { new IEnumerable<string> with
            member __.GetEnumerator() : IEnumerator<string> = getEnumerator()
            member __.GetEnumerator() : IEnumerator = upcast getEnumerator()
        }
    let mutable caught = false
    let t =
        task {
            try
                let mutable index = 0
                do! Task.Yield()
                for x in wrapList do
                    do! Task.Yield()
                    match index with
                    | 0 -> require (x = "a") "wrong first value"
                    | _ -> failtest "uhoh"
                    index <- index + 1
                    do! Task.Yield()
                do! Task.Yield()
                return 1
            with
            | TestException "uhoh" ->
                caught <- true
                return 2
        }
    require (t.Result = 2) "wrong result"
    require caught "didn't catch exception"
    require disposed "never disposed"

[<Fact>]
let testExceptionAttachedToTaskWithoutAwait() =
    let mutable ranA = false
    let mutable ranB = false
    let t =
        task {
            ranA <- true
            failtest "uhoh"
            ranB <- true
        }
    require ranA "didn't run immediately"
    require (not ranB) "ran past exception"
    require (not (isNull t.Exception)) "didn't capture exception"
    require (t.Exception.InnerExceptions.Count = 1) "captured more exceptions"
    require (t.Exception.InnerException = TestException "uhoh") "wrong exception"
    let mutable caught = false
    let mutable ranCatcher = false
    let catcher =
        task {
            try
                ranCatcher <- true
                let! result = t
                return false
            with
            | TestException "uhoh" ->
                caught <- true
                return true
        }
    require ranCatcher "didn't run"
    require catcher.Result "didn't catch"
    require caught "didn't catch"

[<Fact>]
let testExceptionAttachedToTaskWithAwait() =
    let mutable ranA = false
    let mutable ranB = false
    let t =
        task {
            ranA <- true
            failtest "uhoh"
            do! Task.Delay(100)
            ranB <- true
        }
    require ranA "didn't run immediately"
    require (not ranB) "ran past exception"
    require (not (isNull t.Exception)) "didn't capture exception"
    require (t.Exception.InnerExceptions.Count = 1) "captured more exceptions"
    require (t.Exception.InnerException = TestException "uhoh") "wrong exception"
    let mutable caught = false
    let mutable ranCatcher = false
    let catcher =
        task {
            try
                ranCatcher <- true
                let! result = t
                return false
            with
            | TestException "uhoh" ->
                caught <- true
                return true
        }
    require ranCatcher "didn't run"
    require catcher.Result "didn't catch"
    require caught "didn't catch"

[<Fact>]
let testExceptionThrownInFinally() =
    let mutable ranInitial = false
    let mutable ranNext = false
    let mutable ranFinally = 0
    let t =
        task {
            try
                ranInitial <- true
                do! Task.Yield()
                Thread.Sleep(100) // shouldn't be blocking so we should get through to requires before this finishes
                ranNext <- true
            finally
                ranFinally <- ranFinally + 1
                failtest "finally exn!"
        }
    require ranInitial "didn't run initial"
    require (not ranNext) "ran next too early"
    try
        t.Wait()
        require false "shouldn't get here"
    with
    | _ -> ()
    require ranNext "didn't run next"
    require (ranFinally = 1) "didn't run finally exactly once"

[<Fact>]
let test2ndExceptionThrownInFinally() =
    let mutable ranInitial = false
    let mutable ranNext = false
    let mutable ranFinally = 0
    let t =
        task {
            try
                ranInitial <- true
                do! Task.Yield()
                Thread.Sleep(100) // shouldn't be blocking so we should get through to requires before this finishes
                ranNext <- true
                failtest "uhoh"
            finally
                ranFinally <- ranFinally + 1
                failtest "2nd exn!"
        }
    require ranInitial "didn't run initial"
    require (not ranNext) "ran next too early"
    try
        t.Wait()
        require false "shouldn't get here"
    with
    | _ -> ()
    require ranNext "didn't run next"
    require (ranFinally = 1) "didn't run finally exactly once"

[<Fact>]
let testFixedStackWhileLoop() =
    let bigNumber = 10000
    let t =
        task {
            let mutable maxDepth = Nullable()
            let mutable i = 0
            while i < bigNumber do
                i <- i + 1
                do! Task.Yield()
                if i % 100 = 0 then
                    let stackDepth = StackTrace().FrameCount
                    if maxDepth.HasValue && stackDepth > maxDepth.Value then
                        failwith "Stack depth increased!"
                    maxDepth <- Nullable(stackDepth)
            return i
        }
    t.Wait()
    require (t.Result = bigNumber) "didn't get to big number"


[<Fact>]
let testFixedStackForLoop() =
    let bigNumber = 10000
    let mutable ran = false
    let t =
        task {
            let mutable maxDepth = Nullable()
            for i in Seq.init bigNumber id do
                do! Task.Yield()
                if i % 100 = 0 then
                    let stackDepth = StackTrace().FrameCount
                    if maxDepth.HasValue && stackDepth > maxDepth.Value then
                        failwith "Stack depth increased!"
                    maxDepth <- Nullable(stackDepth)
            ran <- true
            return ()
        }
    t.Wait()
    require ran "didn't run all"

[<Fact>]
let testTypeInference() =
    let t1 : string Task =
        task {
            return "hello"
        }
    let t2 =
        task {
            let! s = t1
            return s.Length
        }
    t2.Wait()

[<Fact>]
let testNoStackOverflowWithImmediateResult() =
    let longLoop =
        task {
            let mutable n = 0
            while n < 10_000 do
                n <- n + 1
                return! Task.FromResult(())
        }
    longLoop.Wait()

[<Fact>]
let testNoStackOverflowWithYieldResult() =
    let longLoop =
        task {
            let mutable n = 0
            while n < 10_000 do
                let! _ =
                    task {
                        do! Task.Yield()
                        let! _ = Task.FromResult(0)
                        n <- n + 1
                    }
                n <- n + 1
        }
    longLoop.Wait()

[<Fact>]
let testSmallTailRecursion() =
    let shortLoop =
        task {
            let rec loop n =
                task {
                    // larger N would stack overflow on Mono, eat heap mem on MS .NET
                    if n < 1000 then
                        do! Task.Yield()
                        let! _ = Task.FromResult(0)
                        return! loop (n + 1)
                    else
                        return ()
                }
            return! loop 0
        }
    shortLoop.Wait()

[<Fact>]
let testTryOverReturnFrom() =
    let inner() =
        task {
            do! Task.Yield()
            failtest "inner"
            return 1
        }
    let t =
        task {
            try
                do! Task.Yield()
                return! inner()
            with
            | TestException "inner" -> return 2
        }
    require (t.Result = 2) "didn't catch"

[<Theory>]
[<InlineData(10)>]
[<InlineData(100)>]
[<InlineData(1000)>]
let testUnitTaskSequentially n =
    let dict = System.Collections.Concurrent.ConcurrentDictionary<_,_>()
    let t (i:int) =
        task {
            do! Task.Delay 100
            dict.TryAdd(i,1) |> ignore
        }

    let t2 = task {
        for t in [1..n] |> List.map t do
            do! t
    }
    t2.Wait()
    Assert.Equal(dict.Count, n)

[<Theory>]
[<InlineData(10)>]
[<InlineData(100)>]
[<InlineData(1000)>]
let testUnitTaskSequentiallyInTask n =
    let dict = System.Collections.Concurrent.ConcurrentDictionary<_,_>()
    let t (i:int) =
        task {
            do! Task.Delay 100
            dict.TryAdd(i,1) |> ignore
        }

    task {
        for t in [1..n] |> List.map t do
            do! t
        Assert.Equal(dict.Count, n)
    }

// no need to call this, we just want to check that it compiles w/o warnings
let testTrivialReturnCompiles (x : 'a) : 'a Task =
    task {
        do! Task.Yield()
        return x
    }

// no need to call this, we just want to check that it compiles w/o warnings
let testTrivialTransformedReturnCompiles (x : 'a) (f : 'a -> 'b) : 'b Task =
    task {
        do! Task.Yield()
        return f x
    }

type ITaskThing =
    abstract member Taskify : 'a option -> 'a Task

// no need to call this, we just want to check that it compiles w/o warnings
let testInterfaceUsageCompiles (iface : ITaskThing) (x : 'a) : 'a Task =
    task {
        let! xResult = iface.Taskify (Some x)
        do! Task.Yield()
        return xResult
    }