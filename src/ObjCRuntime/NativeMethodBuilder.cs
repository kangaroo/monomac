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
		private static MethodInfo convertstruct = typeof (Marshal).GetMethod ("StructureToPtr", new Type [] { typeof (object), typeof (IntPtr), typeof (bool) });
		private static MethodInfo buildarray = typeof (NSArray).GetMethod ("FromNSObjects", BindingFlags.Static | BindingFlags.Public);
		private static MethodInfo buildsarray = typeof (NSArray).GetMethod ("FromStrings", BindingFlags.Static | BindingFlags.Public);
#endif
		private static MethodInfo getgenericdelegate = typeof (GenericDelegateFactory).GetMethod ("GetDelegate", BindingFlags.Static | BindingFlags.Public);
		private static MethodInfo delegateinvoke = typeof (Delegate).GetMethod ("DynamicInvoke", BindingFlags.Instance | BindingFlags.Public);
                private static MethodInfo getmethodinfo = typeof (MethodBase).GetMethod ("GetMethodFromHandle", BindingFlags.Public | BindingFlags.Static, null, new Type [] { typeof (RuntimeMethodHandle) }, null);

		private MethodInfo minfo;
		private Type type;
		private Type rettype;
		private bool isstret;
				
		internal NativeMethodBuilder (MethodInfo minfo) : this (minfo, minfo.DeclaringType, (ExportAttribute) Attribute.GetCustomAttribute (minfo.GetBaseDefinition (), typeof (ExportAttribute))) {}

		internal NativeMethodBuilder (MethodInfo minfo, Type type, ExportAttribute ea) {
			if (ea == null)
				throw new ArgumentException ("MethodInfo does not have a export attribute");

			Parameters = minfo.GetParameters ();

			rettype = ConvertReturnType (minfo.ReturnType);

			// FIXME: We should detect if this is in a bound assembly or not and only alloc if needed
			Selector = new Selector (ea.Selector ?? minfo.Name, true).Handle;
			Signature = string.Format ("{0}@:", TypeConverter.ToNative (rettype));

			ConvertParameters (Parameters, minfo.IsStatic, isstret);
			
			DelegateType = CreateDelegateType (rettype, ParameterTypes);

			this.minfo = minfo;
			this.type = type;
		}

		internal override Delegate CreateDelegate () {
			DynamicMethod method = new DynamicMethod (string.Format ("[{0}:{1}]", minfo.DeclaringType, minfo), rettype, ParameterTypes, module, true);
			ILGenerator il = method.GetILGenerator ();
			bool isgeneric = minfo.DeclaringType.IsGenericType;
			int locoffset = isgeneric ? 2 : 0; 

			if (isgeneric) {
				il.DeclareLocal (typeof (Delegate));
				il.DeclareLocal (typeof (object []));
			}

			DeclareLocals (il);
			ConvertArguments (il, locoffset);
#if DUMP_CALLS
			il.Emit (OpCodes.Ldstr, string.Format ("Invoking {0} on a {1}", minfo.ToString (), type.ToString ()));
			il.Emit (OpCodes.Call, typeof (Console).GetMethod ("WriteLine", new Type [] { typeof (string) }));
#endif

			if (isgeneric) {
				il.Emit (OpCodes.Ldarg_0);
				il.Emit (OpCodes.Ldtoken, minfo);
				il.Emit (OpCodes.Call, getmethodinfo);
				il.Emit (OpCodes.Call, getgenericdelegate);
				il.Emit (OpCodes.Stloc, 0);

				il.Emit (OpCodes.Ldc_I4_S, ParameterTypes.Length);
				il.Emit (OpCodes.Newarr, typeof(object));
				il.Emit (OpCodes.Stloc, 1);

				for (int i = ArgumentOffset, j = 0; i < ParameterTypes.Length; i++) {
					il.Emit (OpCodes.Ldloc, 1);
					il.Emit (OpCodes.Ldc_I4_S, i);

					if (Parameters [i-ArgumentOffset].ParameterType.IsByRef && IsWrappedType (Parameters[i-ArgumentOffset].ParameterType.GetElementType ())) {
						il.Emit (OpCodes.Ldloca_S, j+locoffset);
						j++;
					} else if (Parameters [i-ArgumentOffset].ParameterType.IsArray && IsWrappedType (Parameters [i-ArgumentOffset].ParameterType.GetElementType ())) {
						il.Emit (OpCodes.Ldloc, j+locoffset);
						j++;
					} else if (typeof (INativeObject).IsAssignableFrom (Parameters [i-ArgumentOffset].ParameterType) && !IsWrappedType (Parameters [i-ArgumentOffset].ParameterType)) {
						il.Emit (OpCodes.Ldarg, i);
						il.Emit (OpCodes.Newobj, Parameters [i-ArgumentOffset].ParameterType.GetConstructor (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type [] { typeof (IntPtr) }, null));
	                                } else if (Parameters [i-ArgumentOffset].ParameterType == typeof (string)) {
						il.Emit (OpCodes.Ldloc, j+locoffset);
						j++;
					} else if (Parameters [i-ArgumentOffset].ParameterType.IsValueType) {
						il.Emit (OpCodes.Box, ParameterTypes [i]);
					} else {
						il.Emit (OpCodes.Ldarg, i);
					}

					il.Emit (OpCodes.Stelem_Ref);
				}

				il.Emit (OpCodes.Ldloc, 0);
				il.Emit (OpCodes.Ldloc, 1);
				il.Emit (OpCodes.Call, delegateinvoke);
			} else {
				if (!minfo.IsStatic) {
					il.Emit (OpCodes.Ldarg, (isstret ? 1 : 0));
					il.Emit (OpCodes.Castclass, type);
				}
	
				LoadArguments (il, locoffset);
	
				if (minfo.IsVirtual)
					il.Emit (OpCodes.Callvirt, minfo);
				else
					il.Emit (OpCodes.Call, minfo);
			}

			UpdateByRefArguments (il, locoffset);

			if (minfo.ReturnType == typeof (string)) {
				il.Emit (OpCodes.Newobj, newnsstring);
#if !MONOMAC_BOOTSTRAP
			} else if (minfo.ReturnType.IsArray && IsWrappedType (minfo.ReturnType.GetElementType ())) {
				if (minfo.ReturnType.GetElementType () == typeof (string))
					il.Emit (OpCodes.Call, buildsarray);
				else
					il.Emit (OpCodes.Call, buildarray);
			} else if (typeof (INativeObject).IsAssignableFrom (minfo.ReturnType) && !IsWrappedType (minfo.ReturnType)) {
				il.Emit (OpCodes.Call, minfo.ReturnType.GetProperty ("Handle").GetGetMethod ());
			} else if (isstret) {
				il.Emit (OpCodes.Box, minfo.ReturnType);
				il.Emit (OpCodes.Ldarg, 0);
				il.Emit (OpCodes.Ldc_I4, 0);
				il.Emit (OpCodes.Call, convertstruct);
#endif
			}
			il.Emit (OpCodes.Ret);

			return method.CreateDelegate (DelegateType);
		}

		private Type ConvertReturnType (Type type) {
			if (type.IsValueType && !type.IsEnum && type.Assembly != typeof (object).Assembly && Marshal.SizeOf (type) > 8) {
				isstret = true;
				return typeof (void);
			}

			if (type == typeof (string))
				return typeof (NSString);
#if !MONOMAC_BOOTSTRAP
			if (type.IsArray && IsWrappedType (type.GetElementType ()))
				return typeof (NSArray);
#endif
			if (typeof (INativeObject).IsAssignableFrom (type) && !IsWrappedType (type))
				return typeof (IntPtr);

			return type;
		}
	}
}
