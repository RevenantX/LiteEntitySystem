.assembly extern netstandard
{
  .publickeytoken = (CC 7B 13 FF CD 2D DD 51 )
  .ver 2:1:0:0
}
.assembly RefMagic
{
  .custom instance void [netstandard]System.Runtime.CompilerServices.CompilationRelaxationsAttribute::.ctor(int32) = ( 01 00 08 00 00 00 00 00 ) 
  .custom instance void [netstandard]System.Runtime.CompilerServices.RuntimeCompatibilityAttribute::.ctor() = ( 01 00 01 00 54 02 16 57 72 61 70 4E 6F 6E 45 78   // ....T..WrapNonEx
                                                                                                                63 65 70 74 69 6F 6E 54 68 72 6F 77 73 01 )       // ceptionThrows.
  .custom instance void [netstandard]System.Runtime.Versioning.TargetFrameworkAttribute::.ctor(string) = ( 01 00 19 2E 4E 45 54 53 74 61 6E 64 61 72 64 2C   // ....NETStandard,
                                                                                                           56 65 72 73 69 6F 6E 3D 76 32 2E 31 01 00 54 0E   // Version=v2.1..T.
                                                                                                           14 46 72 61 6D 65 77 6F 72 6B 44 69 73 70 6C 61   // .FrameworkDispla
                                                                                                           79 4E 61 6D 65 11 2E 4E 45 54 20 53 74 61 6E 64   // yName..NET Stand
                                                                                                           61 72 64 20 32 2E 31 )                            // ard 2.1              // rdLibrary..
  .permissionset reqmin
             = {[netstandard]System.Security.Permissions.SecurityPermissionAttribute = {property bool 'SkipVerification' = bool(true)}}
  .hash algorithm 0x00008004
  .ver 1:0:0:0
}
.custom instance void [netstandard]System.Security.UnverifiableCodeAttribute::.ctor() = ( 01 00 00 00 ) 
.imagebase 0x10000000
.file alignment 0x00000200
.stackreserve 0x00100000
.subsystem 0x0003       // WINDOWS_CUI
.corflags 0x00000001    //  ILONLY
.module RefMagic.dll

.class interface public abstract auto ansi beforefieldinit LiteEntitySystem.Internal.ISyncVar`1<
    valuetype .ctor (class [netstandard]System.ValueType modreq([netstandard]System.Runtime.InteropServices.UnmanagedType)) T>
{
    .param type T
    .custom instance void [netstandard]System.Runtime.CompilerServices.IsUnmanagedAttribute::.ctor() = (01 00 00 00 )
    .method public hidebysig virtual newslot abstract instance void SvSetDirect(!T 'value') cil managed {}
    .method public hidebysig virtual newslot abstract instance void SvSetDirectAndStorePrev(!T 'value', [out] !T& prevValue) cil managed {}
    .method public hidebysig virtual newslot abstract instance bool SvSetFromAndSync(!T& 'value') cil managed {}
    .method public hidebysig virtual newslot abstract instance void SvSetInterpValue(!T 'value') cil managed {}
}

.class public abstract auto ansi sealed beforefieldinit LiteEntitySystem.Internal.RefMagic extends [netstandard]System.Object
{
    .method public hidebysig static !!T GetFieldValue<T>(object obj, int32 offs) cil managed aggressiveinlining
    {
        ldarg obj
        conv.i
        ldarg offs
        add
        ldobj !!T
        ret
    }

    .method public hidebysig static void SetFieldValue<T>(object obj, int32 offs, !!T 'value') cil managed aggressiveinlining
    {
        ldarg obj
        conv.i
        ldarg offs
        add
        ldarg 'value'
        stobj !!T
        ret
    }

    .method public hidebysig static void SyncVarSetDirect<
        valuetype .ctor (class [netstandard]System.ValueType modreq ([netstandard]System.Runtime.InteropServices.UnmanagedType)) T,
        valuetype .ctor (class LiteEntitySystem.Internal.ISyncVar`1<!!T>, class [netstandard]System.ValueType) TSyncVar>
        (object obj, int32 offs, !!T 'value') cil managed aggressiveinlining
    {
        .param type T
        .custom instance void [netstandard]System.Runtime.CompilerServices.IsUnmanagedAttribute::.ctor() = (01 00 00 00 )

        ldarg obj
        conv.i
        ldarg offs
        add
        ldarg 'value'
        constrained. !!TSyncVar
        callvirt instance void class LiteEntitySystem.Internal.ISyncVar`1<!!T>::SvSetDirect(!0)
        ret
    }

    .method public hidebysig static void SyncVarSetInterp<
        valuetype .ctor (class [netstandard]System.ValueType modreq ([netstandard]System.Runtime.InteropServices.UnmanagedType)) T,
        valuetype .ctor (class LiteEntitySystem.Internal.ISyncVar`1<!!T>, class [netstandard]System.ValueType) TSyncVar>
        (object obj, int32 offs, !!T 'value') cil managed aggressiveinlining
    {
        .param type T
        .custom instance void [netstandard]System.Runtime.CompilerServices.IsUnmanagedAttribute::.ctor() = (01 00 00 00 )

        ldarg obj
        conv.i
        ldarg offs
        add
        ldarg 'value'
        constrained. !!TSyncVar
        callvirt instance void class LiteEntitySystem.Internal.ISyncVar`1<!!T>::SvSetInterpValue(!0)
        ret
    }

    .method public hidebysig static void SyncVarSetDirectAndStorePrev<
        valuetype .ctor (class [netstandard]System.ValueType modreq([netstandard]System.Runtime.InteropServices.UnmanagedType)) T,
        valuetype .ctor (class LiteEntitySystem.Internal.ISyncVar`1<!!T>, [netstandard]System.ValueType) TSyncVar>
        (object obj, int32 offs, !!T 'value', [out] !!T& prevValue) cil managed aggressiveinlining
    {
        .param type T
        .custom instance void [netstandard]System.Runtime.CompilerServices.IsUnmanagedAttribute::.ctor() = ( 01 00 00 00 )

        ldarg obj
        conv.i
        ldarg offs
        add
        ldarg 'value'
        ldarg prevValue
        constrained. !!TSyncVar
        callvirt instance void class LiteEntitySystem.Internal.ISyncVar`1<!!T>::SvSetDirectAndStorePrev(!0, !0&)
        ret
    }

    .method public hidebysig static bool SyncVarSetFromAndSync<
        valuetype .ctor (class [netstandard]System.ValueType modreq([netstandard]System.Runtime.InteropServices.UnmanagedType)) T,
        valuetype .ctor (class LiteEntitySystem.Internal.ISyncVar`1<!!T>, [netstandard]System.ValueType) TSyncVar>
        (object obj, int32 offs, !!T& 'value') cil managed aggressiveinlining
    {
        .param type T
        .custom instance void [netstandard]System.Runtime.CompilerServices.IsUnmanagedAttribute::.ctor() = (01 00 00 00 )

        ldarg obj
        conv.i
        ldarg offs
        add
        ldarg 'value'
        constrained. !!TSyncVar
        callvirt instance bool class LiteEntitySystem.Internal.ISyncVar`1<!!T>::SvSetFromAndSync(!0&)
        ret
    }

    .method public hidebysig static void CopyBlock(void* destination, void* source, unsigned int32 byteCount) cil managed aggressiveinlining
    {
        .maxstack 3
        ldarg.0
        ldarg.1 
        ldarg.2
        cpblk
        ret
    }
}