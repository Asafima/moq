// Copyright (c) 2007, Clarius Consulting, Manas Technology Solutions, InSTEDD.
// All rights reserved. Licensed under the BSD 3-Clause License; see License.txt.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

#if FEATURE_CODEDOM
using System.CodeDom;
using Microsoft.CSharp;
#endif

using Moq.Language;
using Moq.Language.Flow;
using Moq.Properties;
using Moq.Protected;

namespace Moq
{
	/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}"]/*'/>
	public partial class Mock<T> : Mock, IMock<T> where T : class
	{
		private static Type[] inheritedInterfaces;
		private static int serialNumberCounter;

		static Mock()
		{
			inheritedInterfaces =
				typeof(T)
				.GetInterfaces()
				.Where(i => ProxyFactory.Instance.IsTypeVisible(i) && !i.GetTypeInfo().IsImport)
				.ToArray();

			serialNumberCounter = 0;
		}

		private T instance;
		private List<Type> additionalInterfaces;
		private Dictionary<Type, object> configuredDefaultValues;
		private object[] constructorArguments;
		private DefaultValueProvider defaultValueProvider;
		private EventHandlerCollection eventHandlers;
		private InvocationCollection invocations;
		private string name;
		private SetupCollection setups;

		private MockBehavior behavior;
		private bool callBase;
		private Switches switches;

#region Ctors

		/// <summary>
		/// Ctor invoked by AsTInterface exclusively.
		/// </summary>
		[SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "skipInitialize")]
		internal Mock(bool skipInitialize)
		{
			// HACK: this is quick hackish. 
			// In order to avoid having an IMock<T> I relevant members 
			// virtual so that As<TInterface> overrides them (i.e. Interceptor).
			// The skipInitialize parameter is not used at all, and it's 
			// just to differentiate this ctor that should do nothing 
			// from the regular ones which initializes the proxy, etc.
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.ctor()"]/*'/>
		[SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
		public Mock()
			: this(MockBehavior.Default)
		{
		}

#pragma warning disable CS1735 // XML comment has a typeparamref tag, but there is no type parameter by that name
		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.ctor(object[])"]/*'/>
		[SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
#pragma warning restore CS1735 // XML comment has a typeparamref tag, but there is no type parameter by that name
		public Mock(params object[] args)
			: this(MockBehavior.Default, args)
		{
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.ctor(MockBehavior)"]/*'/>
		[SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
		public Mock(MockBehavior behavior)
			: this(behavior, new object[0])
		{
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.ctor(MockBehavior,object[])"]/*'/>
		[SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
		public Mock(MockBehavior behavior, params object[] args)
		{
			Guard.Mockable(typeof(T));

			if (args == null)
			{
				args = new object[] { null };
			}

			this.additionalInterfaces = new List<Type>();
			this.behavior = behavior;
			this.configuredDefaultValues = new Dictionary<Type, object>();
			this.constructorArguments = args;
			this.defaultValueProvider = DefaultValueProvider.Empty;
			this.eventHandlers = new EventHandlerCollection();
			this.invocations = new InvocationCollection();
			this.name = CreateUniqueDefaultMockName();
			this.setups = new SetupCollection();
			this.switches = Switches.Default;

			this.CheckParameters();
		}

		private static string CreateUniqueDefaultMockName()
		{
			var serialNumber = Interlocked.Increment(ref serialNumberCounter).ToString("x8");

			string typeName = typeof (T).FullName;

#if FEATURE_CODEDOM
			if (typeof (T).IsGenericType)
			{
				using (var provider = new CSharpCodeProvider())
				{
					var typeRef = new CodeTypeReference(typeof(T));
					typeName = provider.GetTypeOutput(typeRef);
				}
			}
#endif

			return "Mock<" + typeName + ":" + serialNumber + ">";
		}

		private void CheckParameters()
		{
			if (this.constructorArguments.Length > 0)
			{
				if (typeof(T).GetTypeInfo().IsInterface)
				{
					throw new ArgumentException(Properties.Resources.ConstructorArgsForInterface);
				}
				if (typeof(T).IsDelegate())
				{
					throw new ArgumentException(Properties.Resources.ConstructorArgsForDelegate);
				}
			}
		}

#endregion

#region Properties

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.Behavior"]/*'/>
		public override MockBehavior Behavior => this.behavior;

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.CallBase"]/*'/>
		public override bool CallBase
		{
			get => this.callBase;
			set
			{
				if (value && this.MockedType.IsDelegate())
				{
					throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.CallBaseCannotBeUsedWithDelegateMocks));
				}

				this.callBase = value;
			}
		}

		internal override Dictionary<Type, object> ConfiguredDefaultValues => this.configuredDefaultValues;

		/// <summary>
		/// Gets or sets the <see cref="DefaultValueProvider"/> instance that will be used
		/// e. g. to produce default return values for unexpected invocations.
		/// </summary>
		public override DefaultValueProvider DefaultValueProvider
		{
			get => this.defaultValueProvider;
			set => this.defaultValueProvider = value ?? throw new ArgumentNullException(nameof(value));
		}

		internal override EventHandlerCollection EventHandlers => this.eventHandlers;

		internal override List<Type> AdditionalInterfaces => this.additionalInterfaces;

		internal override InvocationCollection MutableInvocations => this.invocations;

		internal override bool IsObjectInitialized => this.instance != null;

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Object"]/*'/>
		[SuppressMessage("Microsoft.Naming", "CA1716:IdentifiersShouldNotMatchKeywords", MessageId = "Object", Justification = "Exposes the mocked object instance, so it's appropriate.")]
		[SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "The public Object property is the only one visible to Moq consumers. The protected member is for internal use only.")]
		public virtual new T Object
		{
			get { return (T)base.Object; }
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Name"]/*'/>
		public string Name
		{
			get => this.name;
			set => this.name = value;
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.ToString"]/*'/>
		public override string ToString()
		{
			return this.Name;
		}

		[DebuggerStepThrough]
		private void InitializeInstance()
		{
			PexProtector.Invoke(InitializeInstancePexProtected);
		}

		private void InitializeInstancePexProtected()
		{
			// Determine the set of interfaces that the proxy object should additionally implement.
			var additionalInterfaceCount = this.AdditionalInterfaces.Count;
			var interfaces = new Type[1 + additionalInterfaceCount];
			interfaces[0] = typeof(IMocked<T>);
			this.AdditionalInterfaces.CopyTo(0, interfaces, 1, additionalInterfaceCount);

			if (this.IsDelegateMock)
			{
				// We're mocking a delegate.
				// Firstly, get/create an interface with a method whose signature
				// matches that of the delegate.
				var delegateInterfaceType = ProxyFactory.Instance.GetDelegateProxyInterface(typeof(T), out var delegateInterfaceMethod);

				// Then create a proxy for that.
				var delegateProxy = ProxyFactory.Instance.CreateProxy(
					delegateInterfaceType,
					this,
					interfaces,
					this.constructorArguments);

				// Then our instance is a delegate of the desired type, pointing at the
				// appropriate method on that proxied interface instance.
				this.instance = (T)(object)delegateInterfaceMethod.CreateDelegate(typeof(T), delegateProxy);
			}
			else
			{
				this.instance = (T)ProxyFactory.Instance.CreateProxy(
					typeof(T),
					this,
					interfaces,
					this.constructorArguments);
			}
		}

		/// <summary>
		/// Returns the mocked object value.
		/// </summary>
		[SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification = "This is actually the protected virtual implementation of the property Object.")]
		protected override object OnGetObject()
		{
			if (this.instance == null)
			{
				this.InitializeInstance();
			}

			return this.instance;
		}

		internal override Type MockedType
		{
			get { return typeof(T); }
		}

		internal override SetupCollection Setups => this.setups;

		internal override Type TargetType => typeof(T);

		internal override Type[] InheritedInterfaces => Mock<T>.inheritedInterfaces;

		/// <summary>
		/// A set of switches that influence how this mock will operate.
		/// You can opt in or out of certain features via this property.
		/// </summary>
		public override Switches Switches
		{
			get => this.switches;
			set => this.switches = value;
		}

#endregion

#region Setup

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Setup"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public ISetup<T> Setup(Expression<Action<T>> expression)
		{
			var setup = Mock.Setup(this, expression, null);
			return new VoidSetupPhrase<T>(setup);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Setup{TResult}"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public ISetup<T, TResult> Setup<TResult>(Expression<Func<T, TResult>> expression)
		{
			var setup = Mock.Setup(this, expression, null);
			return new NonVoidSetupPhrase<T, TResult>(setup);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.SetupGet"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public ISetupGetter<T, TProperty> SetupGet<TProperty>(Expression<Func<T, TProperty>> expression)
		{
			var setup = Mock.SetupGet(this, expression, null);
			return new NonVoidSetupPhrase<T, TProperty>(setup);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.SetupSet{TProperty}"]/*'/>
		public ISetupSetter<T, TProperty> SetupSet<TProperty>(Action<T> setterExpression)
		{
			var setup = Mock.SetupSet(this, setterExpression, null);
			return new SetterSetupPhrase<T, TProperty>(setup);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.SetupSet"]/*'/>
		public ISetup<T> SetupSet(Action<T> setterExpression)
		{
			var setup = Mock.SetupSet(this, setterExpression, null);
			return new VoidSetupPhrase<T>(setup);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.SetupProperty(property)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		[SuppressMessage("Microsoft.Naming", "CA1716:IdentifiersShouldNotMatchKeywords", MessageId = "Property", Justification = "This sets properties, so it's appropriate.")]
		public Mock<T> SetupProperty<TProperty>(Expression<Func<T, TProperty>> property)
		{
			return this.SetupProperty(property, default(TProperty));
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.SetupProperty(property,initialValue)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		[SuppressMessage("Microsoft.Naming", "CA1716:IdentifiersShouldNotMatchKeywords", MessageId = "Property", Justification = "We're setting up a property, so it's appropriate.")]
		public Mock<T> SetupProperty<TProperty>(Expression<Func<T, TProperty>> property, TProperty initialValue)
		{
			TProperty value = initialValue;
			this.SetupGet(property).Returns(() => value);
			SetupSet(this, property, ItExpr.IsAny<TProperty>()).SetCallbackResponse(new Action<TProperty>(p => value = p));
			return this;
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.SetupAllProperties"]/*'/>
		public Mock<T> SetupAllProperties()
		{
			SetupAllProperties(this);
			return this;
		}

		/// <summary>
		/// Return a sequence of values, once per call.
		/// </summary>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By Design")]
		public ISetupSequentialResult<TResult> SetupSequence<TResult>(Expression<Func<T, TResult>> expression)
		{
			var setup = Mock.SetupSequence(this, expression);
			return new SetupSequencePhrase<TResult>(setup);
		}

		/// <summary>
		/// Performs a sequence of actions, one per call.
		/// </summary>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By Design")]
		public ISetupSequentialAction SetupSequence(Expression<Action<T>> expression)
		{
			var setup = Mock.SetupSequence(this, expression);
			return new SetupSequencePhrase(setup);
		}

#endregion

#region When

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.When"]/*'/>
		public ISetupConditionResult<T> When(Func<bool> condition)
		{
			return new WhenPhrase<T>(this, new Condition(condition));
		}

#endregion

#region Verify

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify(expression)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify(Expression<Action<T>> expression)
		{
			Mock.Verify(this, expression, Times.AtLeastOnce(), null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify(expression,times)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify(Expression<Action<T>> expression, Times times)
		{
			Mock.Verify(this, expression, times, null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify(expression,times)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify(Expression<Action<T>> expression, Func<Times> times)
		{
			Verify(expression, times());
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify(expression,failMessage)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify(Expression<Action<T>> expression, string failMessage)
		{
			Mock.Verify(this, expression, Times.AtLeastOnce(), failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify(expression,times,failMessage)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify(Expression<Action<T>> expression, Times times, string failMessage)
		{
			Mock.Verify(this, expression, times, failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify(expression,times,failMessage)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify(Expression<Action<T>> expression, Func<Times> times, string failMessage)
		{
			Mock.Verify(this, expression, times(), failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify{TResult}(expression)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify<TResult>(Expression<Func<T, TResult>> expression)
		{
			Mock.Verify(this, expression, Times.AtLeastOnce(), null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify{TResult}(expression,times)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify<TResult>(Expression<Func<T, TResult>> expression, Times times)
		{
			Mock.Verify(this, expression, times, null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify{TResult}(expression,times)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify<TResult>(Expression<Func<T, TResult>> expression, Func<Times> times)
		{
			Mock.Verify(this, expression, times(), null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify{TResult}(expression,failMessage)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify<TResult>(Expression<Func<T, TResult>> expression, string failMessage)
		{
			Mock.Verify(this, expression, Times.AtLeastOnce(), failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Verify{TResult}(expression,times,failMessage)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void Verify<TResult>(Expression<Func<T, TResult>> expression, Times times, string failMessage)
		{
			Mock.Verify(this, expression, times, failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifyGet(expression)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void VerifyGet<TProperty>(Expression<Func<T, TProperty>> expression)
		{
			Mock.VerifyGet(this, expression, Times.AtLeastOnce(), null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifyGet(expression,times)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void VerifyGet<TProperty>(Expression<Func<T, TProperty>> expression, Times times)
		{
			Mock.VerifyGet(this, expression, times, null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifyGet(expression,times)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void VerifyGet<TProperty>(Expression<Func<T, TProperty>> expression, Func<Times> times)
		{
			VerifyGet(this, expression, times(), null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifyGet(expression,failMessage)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void VerifyGet<TProperty>(Expression<Func<T, TProperty>> expression, string failMessage)
		{
			Mock.VerifyGet(this, expression, Times.AtLeastOnce(), failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifyGet(expression,times,failMessage)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void VerifyGet<TProperty>(Expression<Func<T, TProperty>> expression, Times times, string failMessage)
		{
			Mock.VerifyGet(this, expression, times, failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifyGet(expression,times,failMessage)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures", Justification = "By design")]
		public void VerifyGet<TProperty>(Expression<Func<T, TProperty>> expression, Func<Times> times, string failMessage)
		{
			VerifyGet(this, expression, times(), failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifySet(expression)"]/*'/>
		public void VerifySet(Action<T> setterExpression)
		{
			Mock.VerifySet(this, setterExpression, Times.AtLeastOnce(), null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifySet(expression,times)"]/*'/>
		public void VerifySet(Action<T> setterExpression, Times times)
		{
			Mock.VerifySet(this, setterExpression, times, null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifySet(expression,times)"]/*'/>
		public void VerifySet(Action<T> setterExpression, Func<Times> times)
		{
			Mock.VerifySet(this, setterExpression, times(), null);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifySet(expression,failMessage)"]/*'/>
		public void VerifySet(Action<T> setterExpression, string failMessage)
		{
			Mock.VerifySet(this, setterExpression, Times.AtLeastOnce(), failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifySet(expression,times,failMessage)"]/*'/>
		public void VerifySet(Action<T> setterExpression, Times times, string failMessage)
		{
			Mock.VerifySet(this, setterExpression, times, failMessage);
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.VerifySet(expression,times,failMessage)"]/*'/>
		public void VerifySet(Action<T> setterExpression, Func<Times> times, string failMessage)
		{
			Mock.VerifySet(this, setterExpression, times(), failMessage);
		}

		/// <summary>
		/// Verifies that there were no calls other than those already verified.
		/// </summary>
		/// <exception cref="MockException">There was at least one invocation not previously verified.</exception>
		public void VerifyNoOtherCalls()
		{
			Mock.VerifyNoOtherCalls(this);
		}

#endregion

#region Raise

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Raise"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1030:UseEventsWhereAppropriate", Justification = "Raises the event, rather than being one.")]
		[SuppressMessage("Microsoft.Usage", "CA2200:RethrowToPreserveStackDetails", Justification = "We want to reset the stack trace to avoid Moq noise in it.")]
		public void Raise(Action<T> eventExpression, EventArgs args)
		{
			var (ev, target) = eventExpression.GetEventWithTarget(this.Object);

			try
			{
				target.DoRaise(ev, args);
			}
			catch (Exception e)
			{
				// Reset stack trace so user gets this call site only.
				throw e;
			}
		}

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock{T}.Raise(args)"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1030:UseEventsWhereAppropriate", Justification = "Raises the event, rather than being one.")]
		[SuppressMessage("Microsoft.Usage", "CA2200:RethrowToPreserveStackDetails", Justification = "We want to reset the stack trace to avoid Moq noise in it.")]
		public void Raise(Action<T> eventExpression, params object[] args)
		{
			var (ev, target) = eventExpression.GetEventWithTarget(this.Object);

			try
			{
				target.DoRaise(ev, args);
			}
			catch (Exception e)
			{
				// Reset stack trace so user gets this call site only.
				throw e;
			}
		}

#endregion
	}
}
