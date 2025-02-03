using System;
using System.Collections.Generic;
using System.Reflection;

namespace LiteEntitySystem.Internal
{
    // A typed callback delegate for open-instance methods:
    //   TOwner = the entity class,
    //   TValue = the field type
    internal delegate void SyncVarCallbackDelegate<in TOwner, in TValue>(TOwner owner, TValue value);

    // The registry that stores (delegate + invoker) keyed by (entType, fieldName)
    internal static class SyncVarCallbackRegistry
    {
        // The dictionary maps (EntityType, fieldName) -> a small record:
        //   typedDelegate: The strongly typed SyncVarCallbackDelegate<TOwner,TValue>
        //   invoker:      Action<Delegate, object, object> that can call typedDelegate
        private static readonly Dictionary<(Type, string), (Delegate typedDelegate, Action<Delegate, object, object>
                invoker)>
            Registry = new();

        /// <summary>
        /// Register a callback for a particular field on a given entity type.
        /// </summary>
        /// <param name="entType">Entity class that declares the method.</param>
        /// <param name="fieldName">The name of the field (unique in that class).</param>
        /// <param name="fieldType">The SyncVar's value type (e.g. int).</param>
        /// <param name="methodName">The user’s callback method name in 'entType'.</param>
        public static void RegisterCallback(Type entType, string fieldName, Type fieldType, string methodName)
        {
            if (methodName == null)
                throw new Exception("OnChangeCallback method name is null!");

            var methodInfo = GetMethodInHierarchy(entType, methodName);

            if (methodInfo == null)
                throw new Exception($"Method '{methodName}' not found in {entType}");

            // Create the strongly typed delegate: e.g. SyncVarCallbackDelegate<MyEntity,int>
            var closedDelegateType = typeof(SyncVarCallbackDelegate<,>).MakeGenericType(entType, fieldType);
            var typedDelegate = methodInfo.CreateDelegate(closedDelegateType);

            // Build or get the "invoker" that casts (object owner, object value) -> typed call
            var invoker = CreateInvokerLambda(entType, fieldType);

            // Store in dictionary
            Registry[(entType, fieldName)] = (typedDelegate, invoker);
        }

        /// <summary>
        /// Create the small "invoker lambda" that can call the typed delegate.
        /// </summary>
        private static Action<Delegate, object, object> CreateInvokerLambda(Type entType, Type fieldType)
        {
            // We'll reflect the 'GenericInvoker<TOwner, TValue>' method below.
            MethodInfo genericInvokerMethod = typeof(SyncVarCallbackRegistry)
                .GetMethod(nameof(GenericInvoker), BindingFlags.Static | BindingFlags.NonPublic);

            if (genericInvokerMethod == null)
                throw new Exception("Cannot find GenericInvoker method!");

            // Close it over (entType, fieldType) -> GenericInvoker<MyEntity,int>, for example
            var closedMethod = genericInvokerMethod.MakeGenericMethod(entType, fieldType);

            // Make a delegate of type 'Action<Delegate, object, object>' from that method
            return (Action<Delegate, object, object>)Delegate.CreateDelegate(
                typeof(Action<Delegate, object, object>),
                closedMethod
            );
        }

        /// <summary>
        /// The method that does the actual cast + invocation:
        ///    (SyncVarCallbackDelegate<TOwner,TValue>)callback
        /// Then calls typedCallback( (TOwner)owner, (TValue)newValue ).
        /// </summary>
        private static void GenericInvoker<TOwner, TValue>(
            Delegate callback,
            object owner,
            object newValue)
        {
            var typedCallback = (SyncVarCallbackDelegate<TOwner, TValue>)callback;
            typedCallback((TOwner)owner, (TValue)newValue);
        }

        /// <summary>
        /// Invoke the callback for the specified entity type + field name,
        /// passing the entity instance (as object) and newValue (as object).
        /// </summary>
        public static bool TryInvoke(Type entType, string fieldName, object owner, object newValue)
        {
            if (Registry.TryGetValue((entType, fieldName), out var data))
            {
                // data.typedDelegate is the strongly typed delegate
                // data.invoker is the small Action<Delegate,object,object> that calls it
                data.invoker(data.typedDelegate, owner, newValue);
                return true;
            }

            return false;
        }

        private static MethodInfo GetMethodInHierarchy(Type type, string methodName)
        {
            while (type != null && type != typeof(object))
            {
                // Search only declared methods on the current 'type'
                var method = type.GetMethod(
                    methodName,
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly
                );

                if (method != null)
                    return method;

                type = type.BaseType;
            }

            return null;
        }
    }
}