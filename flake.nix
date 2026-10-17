{
  description = "Lechonk plays Stardew Valley - Mistral Large 4 harness + SMAPI mod";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" ];
      forSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      # The harness lands here in phase 2, once harness/ has real code:
      #   default = pkgs.buildGoApplication { pname = "lechonk"; src = ./harness; };

      devShells = forSystems (pkgs: {
        default = pkgs.mkShell {
          packages = with pkgs; [
            go           # harness
            dotnet-sdk   # SMAPI mod: dotnet build, ModBuildConfig auto-deploys to Mods/
            curl         # poke the mod's HTTP API by hand
            jq
          ];
        };
      });

      # Runtime requirement, deliberately not a flake input:
      # Stardew Valley via Steam + SMAPI installed in the game folder.
      # Launch goes through Steam's environment (steam-run), not through nix;
      # the harness reads the launch command from SV_SMAPI_LAUNCH_CMD.
    };
}
