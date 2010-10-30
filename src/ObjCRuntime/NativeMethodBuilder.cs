//
// Copyright 2010, Novell, Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using MonoMac.Foundation;

namespace MonoMac.ObjCRuntime {
	internal class NativeMethodBuilder : NativeImplementationBuilder {
		private static ConstructorInfo newnsstring = typeof (NSString).GetConstructor (new Type [] { typeof (string) });
#if !MONOMAC_BOOTSTRAP
		private static MethodInfo buildarray = typeof (NSArray).GetMethod ("FromNSObjects", BindingFlags.Static | BindingFlags.Public);
		private static MethodInfo getobject = typeof (Runtime).GetMethod ("GetNSObject", BindingFlags.Static | BindingFlags.Public);
		private static MethodInfo gethandle = typeof (NSObject).GetMethod ("get_Handle", BindingFlags.Instance | BindingFlags.Public);
#endif
		private static MethodInfo getgenericdelegate = typeof (GenericDelegateFactory).GetMethod ("GetDelegate", BindingFlags.Static | BindingFlags.Public);
		private static MethodInfo delegateinvoke = typeof (Delegate).GetMethod ("DynamicInvoke", BindingFlags.Instance | BindingFlags.Public);
                private static MethodInfo getmethodinfo = typeof (MethodBase).GetMethod ("GetMethodFromHandle", BindingFlags.Public | BindingFlags.Static, null, new Type [] { typeof (RuntimeMethodHandle) }, null);

		private MethodInfo minfo;
		private Type rettype;
		private ParameterInfo [] parms;
				
		internal NativeMethodBuilder (MethodInfo minfo) {
			ExportAttribute ea = (ExportAttribute) Attribute.GetCustomAttribute (minfo.GetBaseDefinition (), typeof (ExportAttribute));

			if (ea == null)
				throw new ArgumentException ("MethodInfo does not have a export attribute");

			parms = minfo.GetParameters ();

			rettype = ConvertReturnType (minfo.ReturnType);
			// FIXME: We should detect if this is in a bound assembly or not and only alloc if needed
			Selector = new Selector (ea.Selector ?? minfo.Name, true).Handle;
			Signature = string.Format ("{0}@:", TypeConverter.ToNative (rettype));
			ParameterTypes = new Type [2 + parms.Length];

			ParameterTypes [0] = typeof (NSObject);
			ParameterTypes [1] = typeof (Selector);

			for (int i = 0; i < parms.Length; i++) {
				if (parms [i].ParameterType.IsByRef && (parms[i].ParameterType.GetElementType ().IsSubclassOf (typeof (NSObject)) || parms[i].ParameterType.GetElementType () == typeof (NSObject)))
					ParameterTypes [i + 2] = typeof (IntPtr).MakeByRefType ();
				else
					ParameterTypes [i + 2] = parms [i].ParameterType;
				// The TypeConverter will emit a ^@ for a byref type that is a NSObject or NSObject subclass in this case
				// If we passed the ParameterTypes [i+2] as would be more logical we would emit a ^^v for that case, which
				// while currently acceptible isn't representative of what obj-c wants.
				Signature += TypeConverter.ToNative (parms [i].ParameterType);
			}
			
			DelegateType = CreateDelegateType (rettype, ParameterTypes);

			this.minfo = minfo;
		}

		internal override Delegate CreateDelegate () {
			DynamicMethod method = new DynamicMethod (Guid.NewGuid ().ToString (), rettype, ParameterTypes, module, true);
			ILGenerator il = method.GetILGenerator ();
			
			if (minfo.DeclaringType.IsGenericType) {
				il.DeclareLocal (typeof (Delegate));
				il.DeclareLocal (typeof (object []));

				il.Emit (OpCodes.Ldarg_0);
				il.Emit (OpCodes.Ldtoken, minfo);
				il.Emit (OpCodes.Call, getmethodinfo);
				il.Emit (OpCodes.Call, getgenericdelegate);
				il.Emit (OpCodes.Stloc, 0);

				il.Emit (OpCodes.Ldc_I4_S, ParameterTypes.Length);
				il.Emit (OpCodes.Newarr, typeof(object));
				il.Emit (OpCodes.Stloc, 1);

				for (int i = 0; i < ParameterTypes.Length; i++) {
					il.Emit (OpCodes.Ldloc, 1);
					il.Emit (OpCodes.Ldc_I4_S, i);
					il.Emit (OpCodes.Ldarg, i);
					
					if (ParameterTypes [i].IsValueType)
						il.Emit (OpCodes.Box, ParameterTypes [i]);

					il.Emit (OpCodes.Stelem_Ref);
				}

				il.Emit (OpCodes.Ldloc, 0);
				il.Emit (OpCodes.Ldloc, 1);
				il.Emit (OpCodes.Call, delegateinvoke);

				if (rettype == typeof (void))
					il.Emit (OpCodes.Pop);
				else if (rettype.IsValueType)
					il.Emit (OpCodes.Unbox);

				il.Emit (OpCodes.Ret);
			} else {
				for (int i = 0; i < parms.Length; i++) {
					if (parms [i].ParameterType.IsByRef && (parms[i].ParameterType.GetElementType ().IsSubclassOf (typeof (NSObject)) || parms[i].ParameterType.GetElementType () == typeof (NSObject))) {
						il.DeclareLocal (parms [i].ParameterType.GetElementType ());
					}
				}

#if !MONOMAC_BOOTSTRAP
				for (int i = 2, j = 0; i < ParameterTypes.Length; i++) {
					if (parms [i-2].ParameterType.IsByRef && (parms[i-2].ParameterType.GetElementType ().IsSubclassOf (typeof (NSObject)) || parms[i-2].ParameterType.GetElementType () == typeof (NSObject))) {
						il.Emit (OpCodes.Ldarg, i);
						il.Emit (OpCodes.Ldind_I);
						il.Emit (OpCodes.Call, getobject);
						il.Emit (OpCodes.Stloc, j++);
					}
				}
#endif

				if (!minfo.IsStatic) {
					il.Emit (OpCodes.Ldarg_0);
					il.Emit (OpCodes.Castclass, minfo.DeclaringType);
				}

				for (int i = 2, j = 0; i < ParameterTypes.Length; i++) {
					if (parms [i-2].ParameterType.IsByRef && (parms[i-2].ParameterType.GetElementType ().IsSubclassOf (typeof (NSObject)) || parms[i-2].ParameterType.GetElementType () == typeof (NSObject)))
						il.Emit (OpCodes.Ldloca_S, j++);
					else
						il.Emit (OpCodes.Ldarg, i);
				}
		
				if (minfo.IsVirtual)
					il.Emit (OpCodes.Callvirt, minfo);
				else
					il.Emit (OpCodes.Call, minfo);

#if !MONOMAC_BOOTSTRAP
				for (int i = 2, j = 0; i < ParameterTypes.Length; i++) {
					if (parms [i-2].ParameterType.IsByRef && (parms[i-2].ParameterType.GetElementType ().IsSubclassOf (typeof (NSObject)) || parms[i-2].ParameterType.GetElementType () == typeof (NSObject))) {
						Label done = il.DefineLabel ();
						il.Emit (OpCodes.Ldloc, j);
						il.Emit (OpCodes.Brfalse, done);
						il.Emit (OpCodes.Ldloc, j++);
						il.Emit (OpCodes.Call, gethandle);
						il.Emit (OpCodes.Ldarg, i);
						il.Emit (OpCodes.Stind_I);
						il.MarkLabel (done);
					}
				}
#endif
				if (rettype == typeof (string)) {
					il.Emit (OpCodes.Newobj, newnsstring);
#if !MONOMAC_BOOTSTRAP
				} else if (rettype.IsArray && (rettype.GetElementType () == typeof (NSObject) || rettype.GetElementType ().IsSubclassOf (typeof (NSObject)))) {
					il.Emit (OpCodes.Call, buildarray);
#endif
				}
				il.Emit (OpCodes.Ret);
			}

			return method.CreateDelegate (DelegateType);
		}

		private Type ConvertReturnType (Type type) {
			if (type == typeof (string))
				return typeof (NSString);
#if !MONOMAC_BOOTSTRAP
			if (type.IsArray && (type.GetElementType () == typeof (NSObject) || type.GetElementType ().IsSubclassOf (typeof (NSObject))))
				return typeof (NSArray);
#endif

			return type;
		}
	}
}
